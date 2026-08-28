using Microsoft.Data.Sqlite;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Хранилище профилей окружения.
/// </summary>
/// <remarks>
/// Главное свойство, которое здесь закрепляется, — <b>активен ровно один профиль</b>,
/// и держится это базой, а не аккуратностью вызывающего кода. Два активных профиля
/// означали бы два набора порогов одновременно: измерения пошли бы с одними, а вердикты
/// считались по другим, и расхождение обнаружилось бы не сразу.
/// </remarks>
public sealed class SqliteProfileStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteProfileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл мог остаться заблокированным — временный каталог уберёт система.
        }
    }

    private SqliteRunStore CreateRunStore() => new(new StorageOptions { DatabasePath = _databasePath });

    private SqliteProfileStore CreateStore() => new(CreateRunStore());

    private static NetworkProfile Profile(string name, string? mac = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Signature = new NetworkSignature { GatewayMac = mac, Subnet = "192.168.1.0/24" },
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    [Fact(DisplayName = "Профиль переживает закрытие продукта")]
    public async Task ProfileSurvivesRestart()
    {
        var profile = Profile("офис", "AA-BB-CC-11-22-33") with
        {
            Description = "главный офис, третий этаж",
            Targets = ["192.168.1.1", "srv-01"],
            Thresholds = [Threshold.Parse("p95 < 50")],
        };

        await CreateStore().SaveAsync(profile);

        SqliteConnection.ClearAllPools();

        var loaded = await CreateStore().GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("офис", loaded!.Name);
        Assert.Equal("главный офис, третий этаж", loaded.Description);
        Assert.Equal(2, loaded.Targets.Count);
        Assert.Single(loaded.Thresholds);
        Assert.Equal("AA-BB-CC-11-22-33", loaded.Signature.GatewayMac);
        Assert.Equal("192.168.1.0/24", loaded.Signature.Subnet);
    }

    [Fact(DisplayName = "Переключение снимает активность с прежнего профиля")]
    public async Task ActivationIsExclusive()
    {
        var store = CreateStore();
        var office = Profile("офис");
        var home = Profile("дом");

        await store.SaveAsync(office);
        await store.SaveAsync(home);

        await store.ActivateAsync(office.Id);
        await store.ActivateAsync(home.Id);

        var all = await store.ListAsync();

        Assert.Single(all, p => p.IsActive);
        Assert.Equal(home.Id, (await store.GetActiveAsync())!.Id);
    }

    [Fact(DisplayName = "Профиль снимается без замены")]
    public async Task ActivationCanBeCleared()
    {
        var store = CreateStore();
        var office = Profile("офис");

        await store.SaveAsync(office);
        await store.ActivateAsync(office.Id);
        await store.ActivateAsync(null);

        Assert.Null(await store.GetActiveAsync());
    }

    [Fact(DisplayName = "Второй активный профиль отвергается базой")]
    public async Task SecondActiveIsRefusedByDatabase()
    {
        // Проверка идёт в обход хранилища намеренно: «активен ровно один» должно
        // держаться уникальным индексом. Инвариант, который держится только
        // дисциплиной кода, ломается первым же обращением мимо неё.
        var store = CreateStore();
        var office = Profile("офис");
        var home = Profile("дом");

        await store.SaveAsync(office);
        await store.SaveAsync(home);
        await store.ActivateAsync(office.Id);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE profiles SET is_active = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", home.Id.ToString());

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact(DisplayName = "Изменение профиля не трогает его активность")]
    public async Task SaveDoesNotChangeActivity()
    {
        // Иначе правка описания у неактивного профиля переключала бы окружение,
        // а правка активного — снимала бы его.
        var store = CreateStore();
        var office = Profile("офис");

        await store.SaveAsync(office);
        await store.ActivateAsync(office.Id);

        await store.SaveAsync(office with { Description = "переехали на пятый этаж", IsActive = false });

        var loaded = await store.GetAsync(office.Id);

        Assert.True(loaded!.IsActive);
        Assert.Equal("переехали на пятый этаж", loaded.Description);
    }

    [Fact(DisplayName = "Точное имя выигрывает у совпадения по началу")]
    public async Task ExactNameWins()
    {
        var store = CreateStore();

        await store.SaveAsync(Profile("офис"));
        await store.SaveAsync(Profile("офис заказчика"));

        var found = await store.FindAsync("офис");

        Assert.NotNull(found);
        Assert.Equal("офис", found!.Name);
    }

    [Fact(DisplayName = "Неоднозначное сокращение — ошибка, а не догадка")]
    public async Task AmbiguousPrefixThrows()
    {
        var store = CreateStore();

        await store.SaveAsync(Profile("офис главный"));
        await store.SaveAsync(Profile("офис филиал"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("офис"));

        Assert.Contains("Уточни имя", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Удаление профиля не задевает соседей")]
    public async Task DeleteRemovesOnlyOne()
    {
        var store = CreateStore();
        var office = Profile("офис");
        var home = Profile("дом");

        await store.SaveAsync(office);
        await store.SaveAsync(home);

        Assert.True(await store.DeleteAsync(office.Id));
        Assert.False(await store.DeleteAsync(office.Id));

        var all = await store.ListAsync();

        Assert.Single(all);
        Assert.Equal("дом", all[0].Name);
    }
}
