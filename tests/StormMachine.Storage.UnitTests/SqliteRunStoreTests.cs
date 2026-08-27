using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Проверки хранилища прогонов.
/// </summary>
/// <remarks>
/// Каждый тест работает со своим файлом во временном каталоге, а не с базой в памяти:
/// проверяется в том числе поведение при повторном открытии файла, которого у базы
/// в памяти просто нет.
/// </remarks>
public sealed class SqliteRunStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteRunStoreTests()
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

    private SqliteRunStore CreateStore(RetentionPolicy? retention = null) => new(new StorageOptions
    {
        DatabasePath = _databasePath,
        Retention = retention ?? RetentionPolicy.Default,
        ApplyRetentionOnStartup = false,
    });

    private static RunDescriptor Descriptor(
        ProbeResultShape shape = ProbeResultShape.ScalarSeries,
        string probeName = "ping") => new()
    {
        Kind = ProbeKind.Icmp,
        ProbeName = probeName,
        Shape = shape,
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
        Parameters = new Dictionary<string, object?> { ["count"] = 3, ["interval"] = 100 },
    };

    private static Sample Ok(int sequence, double value, string? label = null, int? group = null) => new()
    {
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UtcNow,
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = group,
    };

    [Fact]
    public async Task WriteAndRead_RoundTripsEverything()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.5));
            await writer.AppendAsync(Ok(1, 2.5));
            await writer.AppendAsync(Sample.Failed(2, DateTimeOffset.UtcNow, SampleStatus.Timeout));
            await writer.CompleteAsync([ProbeFact.Text("test", "ключ", "значение")], "192.168.1.1", wasCancelled: false);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.Equal("ping", run.Summary.ProbeName);
        Assert.Equal(RunState.Completed, run.Summary.State);
        Assert.Equal(3, run.Summary.SentCount);
        Assert.Equal(2, run.Summary.SuccessCount);
        Assert.Equal("192.168.1.1", run.Summary.ResolvedAddress);
        Assert.Equal("тестовый", run.Context.InterfaceName);
        Assert.Equal(0.25, run.Context.CalibrationBaselineMs, 6);

        var fact = Assert.Single(run.Facts);
        Assert.Equal("значение", fact.Value);

        Assert.Equal(3, run.Samples.Count);
        Assert.Equal(1.5, run.Samples[0].Value, 6);

        // Неуспешный сэмпл не должен читаться как измеренный ноль.
        Assert.True(double.IsNaN(run.Samples[2].Value));

        Assert.Equal("3", run.Parameters["count"]);
    }

    [Fact]
    public async Task InterruptedRun_KeepsSamplesAndIsMarkedAbandoned()
    {
        // Главное требование итерации: измеренное не теряется, даже если итог
        // подвести не успели.
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;

            for (var i = 0; i < 25; i++)
            {
                await writer.AppendAsync(Ok(i, 1.0 + i));
            }

            // CompleteAsync намеренно не вызывается — имитация падения процесса.
        }

        // Отметка жизни отодвигается назад: без этого прогон выглядел бы живым,
        // и брошенным его признали бы только через отведённый срок молчания.
        Backdate(id, TimeSpan.FromHours(1));

        // Повторное открытие хранилища: именно здесь брошенные прогоны помечаются.
        var reopened = CreateStore();
        await reopened.InitializeAsync();

        var run = await reopened.GetAsync(id);

        Assert.NotNull(run);
        Assert.Equal(RunState.Abandoned, run.Summary.State);
        Assert.Equal(25, run.Samples.Count);
        Assert.Equal(25, run.Summary.SentCount);

        // Агрегаты записать было некому — они считаются при чтении.
        Assert.NotEmpty(run.Series);
        Assert.NotNull(run.Summary.MedianMs);
    }

    /// <summary>
    /// Живой прогон не должен объявляться прерванным из-за чужого процесса.
    /// </summary>
    /// <remarks>
    /// Пометка брошенных срабатывает при открытии хранилища, а хранилище открывает
    /// каждый клиент. Консоль, запущенная рядом с приложением, объявляла бы чужое
    /// идущее измерение прерванным сбоем. С разовыми пробами по секунде это почти
    /// не встречалось; с часовым MTR стало обычным делом.
    /// </remarks>
    [Fact]
    public async Task LiveRun_IsNotMistakenForAbandoned()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await using var writer = await store.BeginRunAsync(Descriptor());

        for (var i = 0; i < 5; i++)
        {
            await writer.AppendAsync(Ok(i, 1.0 + i));
        }

        // Второй процесс открывает ту же базу, пока первый ещё пишет.
        var other = CreateStore();
        await other.InitializeAsync();

        var run = await other.GetAsync(writer.RunId);

        Assert.NotNull(run);
        Assert.Equal(RunState.Running, run.Summary.State);
    }

    /// <summary>Отодвигает отметку жизни прогона назад — имитация молчания.</summary>
    private void Backdate(Guid id, TimeSpan age)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET heartbeat_ticks = $ticks WHERE id = $id;";
        command.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.Subtract(age).UtcTicks);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task CancelledRun_IsMarkedCancelled()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: true);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.Equal(RunState.Cancelled, run.Summary.State);
        Assert.Single(run.Samples);
    }

    [Fact]
    public async Task PhasedRun_StoresSeriesPerPhase()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor(ProbeResultShape.PhasedTiming, "http")))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 10, "dns", 0));
            await writer.AppendAsync(Ok(0, 40, "connect", 0));
            await writer.AppendAsync(Ok(0, 60, "tls", 0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);

        // Первый ряд — весь прогон, дальше фазы в порядке появления.
        Assert.Equal(4, run.Series.Count);
        Assert.Equal(SeriesBreakdown.WholeRunKey, run.Series[0].Key);
        Assert.Equal("dns", run.Series[1].Key);
        Assert.Equal("connect", run.Series[2].Key);
        Assert.Equal("tls", run.Series[3].Key);
        Assert.Equal(60, run.Series[3].Statistics.P50Ms, 6);
    }

    [Fact]
    public async Task PhasedRun_SamplesShareSequenceNumber()
    {
        // Ключ таблицы сэмплов не может быть (прогон, порядковый номер): у фазовых проб
        // номер повторяется. Тест закрепляет это — иначе схема сломается молча.
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor(ProbeResultShape.PhasedTiming, "http")))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 10, "dns", 0));
            await writer.AppendAsync(Ok(0, 40, "connect", 0));
            await writer.AppendAsync(Ok(0, 60, "tls", 0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.Equal(3, run.Samples.Count);
        Assert.All(run.Samples, s => Assert.Equal(0, s.Sequence));
    }

    [Fact]
    public async Task PathTraceRun_StoresSeriesPerHop()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor(ProbeResultShape.PathTrace, "trace")))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0, "10.0.0.1", 1) with { RespondedBy = "10.0.0.1" });
            await writer.AppendAsync(Ok(1, 1.2, "10.0.0.1", 1) with { RespondedBy = "10.0.0.1" });
            await writer.AppendAsync(Sample.Failed(2, DateTimeOffset.UtcNow, SampleStatus.Timeout) with { Group = 2 });
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.Contains(run.Series, s => s.Key == "hop:1");
        Assert.Contains(run.Series, s => s.Key == "hop:2");

        var silent = run.Series.First(s => s.Key == "hop:2");
        Assert.Equal(0, silent.SuccessCount);
        Assert.Equal(100, silent.LossPercent, 6);
    }

    [Fact]
    public async Task Retention_DropsRawSamplesButKeepsAggregates()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.AppendAsync(Ok(1, 3.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        // Горизонт в прошлом — прогон считается состарившимся немедленно.
        var report = await store.ApplyRetentionAsync(new RetentionPolicy
        {
            RawSampleHorizon = TimeSpan.Zero,
            RunHorizon = TimeSpan.FromDays(365),
        });

        Assert.Equal(1, report.RunsDownsampled);
        Assert.Equal(2, report.SamplesDeleted);
        Assert.Equal(0, report.RunsDeleted);

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.False(run.Summary.HasRawSamples);
        Assert.Empty(run.Samples);

        // Ради этого политика и устроена так: цифры остаются, подробности уходят.
        Assert.NotEmpty(run.Series);
        Assert.Equal(2, run.Series[0].SentCount);
        Assert.Equal(1.0, run.Series[0].Statistics.MinMs, 6);
        Assert.Equal(3.0, run.Series[0].Statistics.MaxMs, 6);
    }

    [Fact]
    public async Task Retention_DryRunChangesNothing()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var report = await store.ApplyRetentionAsync(
            new RetentionPolicy { RawSampleHorizon = TimeSpan.Zero, RunHorizon = TimeSpan.Zero },
            dryRun: true);

        Assert.Equal(1, report.RunsDeleted);

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.True(run.Summary.HasRawSamples);
        Assert.Single(run.Samples);
    }

    [Fact]
    public async Task Delete_RemovesSamplesToo()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        Assert.True(await store.DeleteAsync(id));
        Assert.Null(await store.GetAsync(id));

        var (_, runs, samples) = await store.GetUsageAsync();

        Assert.Equal(0, runs);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task List_FiltersByProbeAndOrdersNewestFirst()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        foreach (var name in new[] { "ping", "http", "ping" })
        {
            await using var writer = await store.BeginRunAsync(Descriptor(probeName: name));
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var all = await store.ListAsync(new RunQuery { Limit = 10 });
        Assert.Equal(3, all.Count);

        var pings = await store.ListAsync(new RunQuery { Limit = 10, ProbeName = "ping" });
        Assert.Equal(2, pings.Count);
        Assert.All(pings, r => Assert.Equal("ping", r.ProbeName));

        Assert.True(all[0].StartedUtc >= all[^1].StartedUtc, "Список должен идти от новых к старым.");
    }

    [Fact]
    public async Task LargeRun_FlushesInBatches()
    {
        // Пачка равна 200 сэмплам; берём заведомо больше, чтобы сброс случился не раз.
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;

            for (var i = 0; i < 1000; i++)
            {
                await writer.AppendAsync(Ok(i, i % 17 + 0.5));
            }

            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var run = await store.GetAsync(id);

        Assert.NotNull(run);
        Assert.Equal(1000, run.Samples.Count);

        // Порядок поступления обязан сохраниться: по нему строятся графики.
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(i, run.Samples[i].Sequence);
        }
    }

    [Fact]
    public async Task Reopening_KeepsData()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Guid id;
        await using (var writer = await store.BeginRunAsync(Descriptor()))
        {
            id = writer.RunId;
            await writer.AppendAsync(Ok(0, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var reopened = CreateStore();
        await reopened.InitializeAsync();

        var run = await reopened.GetAsync(id);

        Assert.NotNull(run);
        Assert.Single(run.Samples);
        Assert.Equal(RunState.Completed, run.Summary.State);
    }
}
