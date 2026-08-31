using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Storage;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Сохранённая политика хранения.
/// </summary>
/// <remarks>
/// Главное свойство — политика <b>действует</b>, а не только показывается: уборка
/// на старте хранилища обязана слушаться сохранённых горизонтов, иначе экранная
/// форма (И-24) была бы надписью поверх прежних умолчаний.
/// </remarks>
public sealed class RetentionSettingsTests : IDisposable
{
    private sealed class NoProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;

        public string? Unprotect(string protectedValue) => protectedValue;
    }

    private readonly string _directory;
    private readonly string _databasePath;

    public RetentionSettingsTests()
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

    [Fact(DisplayName = "Без сохранённого — умолчания")]
    public async Task WithoutStoredValues_DefaultsApply()
    {
        var policy = await CreateSettings().GetAsync();

        Assert.Equal(RetentionPolicy.Default.RawSampleHorizon, policy.RawSampleHorizon);
        Assert.Equal(RetentionPolicy.Default.RunHorizon, policy.RunHorizon);
    }

    [Fact(DisplayName = "Сохранённое возвращается и переживает переоткрытие")]
    public async Task StoredValues_RoundTrip()
    {
        await CreateSettings().SetAsync(rawDays: 30, runDays: 180);

        var policy = await CreateSettings().GetAsync();

        Assert.Equal(TimeSpan.FromDays(30), policy.RawSampleHorizon);
        Assert.Equal(TimeSpan.FromDays(180), policy.RunHorizon);
    }

    [Fact(DisplayName = "Прогоны короче сырья — отклоняется с объяснением")]
    public async Task RunsShorterThanSamples_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => CreateSettings().SetAsync(rawDays: 90, runDays: 30));

        Assert.Contains("сироты", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Уборка на старте слушается сохранённой политики, а не умолчаний.
    /// </summary>
    /// <remarks>
    /// Прогон старится на 400 дней — по умолчанию (365) он был бы удалён при первом
    /// же открытии. С сохранённым горизонтом в 1000 дней он обязан пережить старт,
    /// а после смены горизонта на 30 — исчезнуть.
    /// </remarks>
    [Fact(DisplayName = "Уборка на старте слушается сохранённой политики")]
    public async Task StartupCleanup_ObeysStoredPolicy()
    {
        var id = await WriteRunAsync();
        Backdate(id, days: 400);
        await CreateSettings().SetAsync(rawDays: 900, runDays: 1000);

        await CreateStore().InitializeAsync();
        Assert.NotNull(await CreateStore().GetAsync(id));

        await CreateSettings().SetAsync(rawDays: 10, runDays: 30);

        await CreateStore().InitializeAsync();
        Assert.Null(await CreateStore().GetAsync(id));
    }

    private RetentionSettings CreateSettings() =>
        new(new SqliteSettingsStore(CreateStore(applyRetention: false), new NoProtector()));

    private SqliteRunStore CreateStore(bool applyRetention = true) => new(new StorageOptions
    {
        DatabasePath = _databasePath,
        ApplyRetentionOnStartup = applyRetention,
    });

    private void Backdate(Guid id, int days)
    {
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET started_ticks = $ticks, heartbeat_ticks = $ticks WHERE id = $id;";
        command.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.AddDays(-days).UtcTicks);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    private async Task<Guid> WriteRunAsync()
    {
        var store = CreateStore(applyRetention: false);
        await store.InitializeAsync();

        await using var writer = await store.BeginRunAsync(new RunDescriptor
        {
            Kind = ProbeKind.Icmp,
            ProbeName = "ping",
            Shape = ProbeResultShape.ScalarSeries,
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = new MeasurementContext
            {
                InterfaceName = "тестовый",
                AdapterKind = AdapterKind.Physical,
                CalibrationBaselineMs = 0.25,
                ProductVersion = "0.0.0-test",
                Methodology = Methodology.IcmpEcho,
                StartedUtc = DateTimeOffset.UtcNow,
            },
        });

        await writer.AppendAsync(new Sample
        {
            Sequence = 0,
            TimestampUtc = DateTimeOffset.UtcNow,
            Value = 1.0,
            Status = SampleStatus.Success,
        });

        await writer.CompleteAsync([], resolvedAddress: null, wasCancelled: false);

        return writer.RunId;
    }
}
