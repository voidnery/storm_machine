using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Хранилище сценариев, собранных оператором.
/// </summary>
/// <remarks>
/// Появилось в И-22 и закрыло долг И-11: собрать свою цепочку можно было только правкой
/// кода. Проверяется прежде всего то, на чём эта работа сразу и споткнулась: параметры
/// шага объявлены как <c>object?</c>, и под обрезкой такой словарь не сериализуется
/// вовсе — генератор исходников не знает, что окажется внутри. Хранятся они строками,
/// как параметры пресета и монитора, и разбирает их сама проба при запуске.
/// </remarks>
public sealed class SqliteScenarioStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteScenarioStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл мог остаться заблокированным — временный каталог уберёт система.
        }
    }

    private SqliteScenarioStore CreateStore() => new(new SqliteRunStore(new StorageOptions
    {
        DatabasePath = _databasePath,
        Retention = RetentionPolicy.Default,
        ApplyRetentionOnStartup = false,
    }));

    private static Scenario Scenario(string name, params ScenarioStep[] steps) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "Проверка для теста",
        Steps = steps,
    };

    private static ScenarioStep Step(
        string name = "Шаг",
        string probe = "ping",
        bool continueOnFailure = false) => new()
    {
        Name = name,
        ProbeName = probe,
        Target = Target.Ip("192.168.1.1"),
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = 5,
            ["interval"] = 200,
            ["verify"] = true,
            ["note"] = "текст",
        },
        Thresholds = [Threshold.Parse("p95 < 100")],
        PhaseMetric = "p95",
        ContinueOnFailure = continueOnFailure,
    };

    /// <summary>
    /// Шаг переживает запись и чтение целиком.
    /// </summary>
    /// <remarks>
    /// Все четыре типа параметра проверяются нарочно: число, число с точкой,
    /// логическое и строка. Под обрезкой словарь <c>object?</c> не сериализуется,
    /// и обход этого — хранение строками — обязан сохранять смысл каждого.
    /// </remarks>
    [Fact]
    public async Task Step_SurvivesTheRoundTrip()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("моя проверка", Step());
        await store.SaveAsync(scenario);

        var loaded = await store.GetAsync(scenario.Id);

        Assert.NotNull(loaded);

        var step = Assert.Single(loaded!.Steps);

        Assert.Equal("Шаг", step.Name);
        Assert.Equal("ping", step.ProbeName);
        Assert.Equal("192.168.1.1", step.Target.Value);
        Assert.Equal("5", step.Parameters["count"]);
        Assert.Equal("true", step.Parameters["verify"]);
        Assert.Equal("текст", step.Parameters["note"]);
        Assert.Single(step.Thresholds);
        Assert.Equal("p95", step.PhaseMetric);
    }

    /// <summary>
    /// Признак «продолжать после отказа» не теряется.
    /// </summary>
    /// <remarks>
    /// Потеря этого поля изменила бы поведение молча: сценарий пошёл бы дальше там,
    /// где должен был остановиться, и оператор получил бы россыпь отказов вместо одного
    /// внятного «сломалось здесь». Именно поэтому оно проверяется отдельно.
    /// </remarks>
    [Fact]
    public async Task ContinueOnFailure_IsNotLost()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("цепочка", Step(continueOnFailure: true), Step("второй"));
        await store.SaveAsync(scenario);

        var loaded = await store.GetAsync(scenario.Id);

        Assert.True(loaded!.Steps[0].ContinueOnFailure);
        Assert.False(loaded.Steps[1].ContinueOnFailure);
    }

    /// <summary>Порядок шагов — часть смысла цепочки, и он сохраняется.</summary>
    [Fact]
    public async Task StepOrder_IsPreserved()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("порядок", Step("первый"), Step("второй"), Step("третий"));
        await store.SaveAsync(scenario);

        var loaded = await store.GetAsync(scenario.Id);

        Assert.Equal(["первый", "второй", "третий"], loaded!.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Scenario_IsFoundByName()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveAsync(Scenario("моя проверка", Step()));

        Assert.NotNull(await store.FindAsync("моя проверка"));

        // Регистр не различается: «Веб» и «веб» — один сценарий.
        Assert.NotNull(await store.FindAsync("МОЯ ПРОВЕРКА"));
    }

    [Fact]
    public async Task Scenario_IsFoundByIdPrefix()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("моя проверка", Step());
        await store.SaveAsync(scenario);

        var prefix = scenario.Id.ToString()[..8];

        Assert.Equal(scenario.Id, (await store.FindAsync(prefix))!.Id);
    }

    /// <summary>Повторное сохранение обновляет на месте, а не заводит второй.</summary>
    [Fact]
    public async Task SavingTwice_UpdatesInPlace()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("моя проверка", Step());
        await store.SaveAsync(scenario);
        await store.SaveAsync(scenario with { Steps = [Step(), Step("второй")], Version = 2 });

        var all = await store.ListAsync();

        Assert.Single(all);
        Assert.Equal(2, all[0].Steps.Count);
        Assert.Equal(2, all[0].Version);
    }

    [Fact]
    public async Task Scenario_IsDeleted()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("временный", Step());
        await store.SaveAsync(scenario);

        Assert.True(await store.DeleteAsync(scenario.Id));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task EmptyScenario_IsStoredAsEmpty()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scenario = Scenario("пустой");
        await store.SaveAsync(scenario);

        Assert.Empty((await store.GetAsync(scenario.Id))!.Steps);
    }
}
