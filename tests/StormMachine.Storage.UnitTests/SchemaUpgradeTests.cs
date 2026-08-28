using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Presets;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Обновление схемы на базе, в которой уже есть данные.
/// </summary>
/// <remarks>
/// Проверок этого не было до И-15, и это был настоящий пробел: остальные тесты
/// всегда начинают с пустого файла, то есть проверяют создание, а не обновление.
/// А у оператора файл не пустой — в нём год измерений, и ровно на нём обновление
/// либо сработает, либо потеряет всё.
/// <para>
/// Отметка версии отматывается назад намеренно: это единственный способ заставить
/// ступени выполниться повторно, не таская в репозиторий двоичный файл базы
/// прошлого выпуска.
/// </para>
/// </remarks>
public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SchemaUpgradeTests()
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

    private SqliteRunStore CreateStore() => new(new StorageOptions { DatabasePath = _databasePath });

    /// <summary>Отматывает отметку версии, оставляя таблицы и данные на месте.</summary>
    private void Rewind(int version)
    {
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($v);";
        command.Parameters.AddWithValue("$v", version);
        command.ExecuteNonQuery();
    }

    private int VersionOf()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        return StorageSchema.ReadVersion(connection);
    }

    [Fact(DisplayName = "Повторное обновление с любой ступени не роняет базу")]
    public async Task UpgradeIsRepeatable()
    {
        await CreateStore().InitializeAsync();

        Assert.Equal(StorageSchema.CurrentVersion, VersionOf());

        // Каждая ступень проходится заново по уже существующим таблицам. Ступень,
        // которая этого не переживает, сломает продукт у того, кого прервали
        // посреди обновления.
        for (var from = 1; from < StorageSchema.CurrentVersion; from++)
        {
            Rewind(from);

            await CreateStore().InitializeAsync();

            Assert.Equal(StorageSchema.CurrentVersion, VersionOf());
        }
    }

    [Fact(DisplayName = "Данные переживают обновление схемы")]
    public async Task DataSurvivesUpgrade()
    {
        var runStore = CreateStore();
        await runStore.InitializeAsync();

        var presets = new SqlitePresetStore(runStore);
        var monitors = new SqliteMonitorStore(runStore);

        var preset = new Preset
        {
            Id = Guid.NewGuid(),
            Name = "шлюз",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Version = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        var monitor = new Monitor
        {
            Id = Guid.NewGuid(),
            Name = "доступность шлюза",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
            Thresholds = [Threshold.Parse("loss < 1")],
        };

        await presets.SaveAsync(preset);
        await monitors.SaveAsync(monitor);

        // Продукт закрыли на старой версии и открыли на новой.
        Rewind(7);

        var reopened = CreateStore();
        await reopened.InitializeAsync();

        Assert.Equal(StorageSchema.CurrentVersion, VersionOf());
        Assert.NotNull(await new SqlitePresetStore(reopened).GetAsync(preset.Id));
        Assert.NotNull(await new SqliteMonitorStore(reopened).GetAsync(monitor.Id));
    }

    [Fact(DisplayName = "База более новой версии отвергается с внятным объяснением")]
    public async Task NewerSchemaIsRefused()
    {
        await CreateStore().InitializeAsync();

        Rewind(StorageSchema.CurrentVersion + 1);

        // Молча открыть такую базу значило бы читать её по старым правилам
        // и незаметно портить.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore().InitializeAsync());

        Assert.Contains("более новой версией", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Эталон переживает удаление прогона, с которого снят")]
    public async Task BaselineOutlivesItsRun()
    {
        var runStore = CreateStore();
        await runStore.InitializeAsync();

        var baselines = new SqliteBaselineStore(runStore);
        var run = Guid.NewGuid();

        var baseline = new Baseline
        {
            Id = Guid.NewGuid(),
            Name = "норма",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Unit = Domain.Measurements.MeasurementUnit.Milliseconds,
            Context = new Domain.Measurements.MeasurementContext
            {
                InterfaceName = "Ethernet",
                AdapterKind = Domain.Measurements.AdapterKind.Physical,
                CalibrationBaselineMs = 0.2,
                ProductVersion = "0.1.0",
                Methodology = Domain.Measurements.Methodology.IcmpEcho,
                StartedUtc = DateTimeOffset.UtcNow,
            },
            Metrics = [new BaselineMetric("p95", 12.5, HigherIsBetter: false)],
            RunId = run,
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        await baselines.SaveAsync(baseline);

        // Ссылка на прогон намеренно без внешнего ключа: политика хранения удаляет
        // старые прогоны, а эталон заводят ради того, чтобы сравнивать с ним годами.
        await runStore.DeleteAsync(run);

        var loaded = await baselines.GetAsync(baseline.Id);

        Assert.NotNull(loaded);
        Assert.Equal(run, loaded!.RunId);
        Assert.Equal(Domain.Measurements.AdapterKind.Physical, loaded.Context.AdapterKind);
    }
}
