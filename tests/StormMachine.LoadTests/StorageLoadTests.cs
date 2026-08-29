using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using StormMachine.Storage;

namespace StormMachine.LoadTests;

/// <summary>
/// Журнал прогонов за год.
/// </summary>
/// <remarks>
/// Год — не круглое число для красоты: политика хранения держит прогоны 365 дней,
/// а сырые сэмплы 90, и именно к концу первого года база впервые оказывается в том
/// состоянии, ради которого политика писалась. До И-19 хранилище проверялось на
/// десятках записей, где любой запрос быстр независимо от того, как он написан.
/// <para>
/// Монитор с интервалом пять минут даёт 105 120 проверок в год. Это и есть та
/// нагрузка, которую продукт создаёт сам себе, работая как задумано.
/// </para>
/// </remarks>
[Trait("Категория", "Нагрузка")]
public sealed class StorageLoadTests : IDisposable
{
    /// <summary>Проверок за год при интервале пять минут.</summary>
    private const int RunsPerYear = 105_120;

    /// <summary>Сэмплов на проверку — обычный ping монитора.</summary>
    private const int SamplesPerRun = 10;

    private readonly string _directory;
    private readonly string _databasePath;

    public StorageLoadTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-load", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        Measured.Save("load-storage.txt");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог уберёт система.
        }
    }

    /// <summary>
    /// Год работы монитора: запись, чтение журнала, открытие прогона, уборка.
    /// </summary>
    /// <remarks>
    /// Один прогон вместо четырёх отдельных проверок: наполнение базы стоит дороже
    /// всего остального вместе взятого, и повторять его четырежды ради формальной
    /// раздельности значило бы вчетверо удлинить прогон, ничего не узнав дополнительно.
    /// </remarks>
    [Fact]
    public async Task AYearOfMonitoring_StaysUsable()
    {
        Measured.Note($"Журнал прогонов за год: {RunsPerYear:N0} прогонов по {SamplesPerRun} сэмплов");

        var store = new SqliteRunStore(new StorageOptions
        {
            DatabasePath = _databasePath,
            Retention = RetentionPolicy.Default,
            ApplyRetentionOnStartup = false,
        });

        await store.InitializeAsync();

        // ---------------------------------------------------------------- запись
        var start = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);

        await Measured.RunAsync("запись года прогонов", async () =>
        {
            for (var i = 0; i < RunsPerYear; i++)
            {
                var at = start.AddMinutes(5 * i);
                await using var writer = await store.BeginRunAsync(Descriptor(at));

                for (var s = 0; s < SamplesPerRun; s++)
                {
                    await writer.AppendAsync(new Sample
                    {
                        Sequence = s,
                        TimestampUtc = at.AddMilliseconds(s * 100),

                        // Значения не случайны: прогон, меряющий каждый раз другое,
                        // не с чем сравнивать. Пила даёт разброс без генератора.
                        Value = 1.0 + ((i + s) % 40) / 10.0,
                        Status = SampleStatus.Success,
                    });
                }

                await writer.CompleteAsync([], "192.168.1.1", wasCancelled: false);
            }

            return true;
        });

        var sizeBefore = new FileInfo(_databasePath).Length;
        var usage = await store.GetUsageAsync();

        Measured.Note(
            $"  файл базы: {Measured.Megabytes(sizeBefore)} МБ, "
            + $"прогонов {usage.RunCount:N0}, сэмплов {usage.SampleCount:N0}");

        Assert.Equal(RunsPerYear, usage.RunCount);

        // ------------------------------------------------------- чтение журнала
        // Оператор открывает журнал и ждёт список. Полсекунды здесь — уже заметная
        // задержка на действии, которое он делает десятки раз за сессию.
        var (page, listElapsed, _) = await Measured.RunAsync(
            "первая страница журнала (20 записей)",
            () => store.ListAsync(new RunQuery { Limit = 20 }));

        Assert.Equal(20, page.Count);

        Assert.True(
            listElapsed < TimeSpan.FromMilliseconds(500),
            $"Журнал за год открывался {listElapsed.TotalMilliseconds:N0} мс — это заметная задержка.");

        // Список с фильтром идёт по составному индексу; если бы его не было,
        // разница с запросом без фильтра была бы кратной.
        var (filtered, filterElapsed, _) = await Measured.RunAsync(
            "журнал с фильтром по пробе",
            () => store.ListAsync(new RunQuery { Limit = 20, ProbeName = "ping" }));

        Assert.Equal(20, filtered.Count);

        Assert.True(
            filterElapsed < TimeSpan.FromMilliseconds(500),
            $"Фильтр по журналу за год отработал за {filterElapsed.TotalMilliseconds:N0} мс.");

        // ------------------------------------------------------ открытие прогона
        var (stored, openElapsed, _) = await Measured.RunAsync(
            "открытие одного прогона со всеми сэмплами",
            () => store.GetAsync(page[0].Id));

        Assert.NotNull(stored);
        Assert.Equal(SamplesPerRun, stored.Samples.Count);

        Assert.True(
            openElapsed < TimeSpan.FromMilliseconds(500),
            $"Один прогон из базы за год открывался {openElapsed.TotalMilliseconds:N0} мс. "
            + "Похоже на просмотр таблицы целиком вместо обращения по ключу.");

        // ------------------------------------------------------------- уборка
        // Состаривается политика, а не данные, и причина в устройстве хранилища:
        // время начала прогона хранилище ставит само, по факту записи. Это верно —
        // прогон начался тогда, когда начался, — но означает, что задним числом
        // базу через открытый интерфейс не наполнить. Тем же приёмом пользуются
        // и обычные проверки хранилища.
        //
        // Нулевой горизонт делает пригодными к уборке сразу все 105 120 прогонов.
        // Для нагрузочного прогона это удача: получается худший случай, а не средний.
        var (report, retentionElapsed, _) = await Measured.RunAsync(
            "уборка: все сэмплы года разом (худший случай)",
            () => store.ApplyRetentionAsync(new RetentionPolicy
            {
                RawSampleHorizon = TimeSpan.Zero,
                RunHorizon = TimeSpan.FromDays(365),
            }));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var sizeAfter = new FileInfo(_databasePath).Length;

        Measured.Note(
            $"  удалено сэмплов {report.SamplesDeleted:N0}, прогонов {report.RunsDeleted:N0}; "
            + $"файл {Measured.Megabytes(sizeBefore)} МБ -> {Measured.Megabytes(sizeAfter)} МБ");

        Measured.Note(string.Empty);

        // Главное утверждение про политику: сэмплы старше 90 дней уходят,
        // а прогоны с агрегатами остаются — иначе отчёт за год окажется пустым.
        Assert.Equal(SamplesPerRun * (long)RunsPerYear, report.SamplesDeleted);
        Assert.Equal(RunsPerYear, report.RunsDownsampled);

        var after = await store.GetUsageAsync();

        Assert.Equal(RunsPerYear, after.RunCount);

        Assert.True(
            retentionElapsed < TimeSpan.FromMinutes(2),
            $"Уборка шла {retentionElapsed.TotalSeconds:N0} с. Она идёт при запуске "
            + "и столько ждать запуска оператор не будет.");

        // Ради этого политика и устроена так: подробности уходят, цифры остаются.
        // Отчёт за год обязан считаться и после уборки.
        var survivor = await store.GetAsync(page[0].Id);

        Assert.NotNull(survivor);
        Assert.Empty(survivor.Samples);
        Assert.NotEmpty(survivor.Series);
    }

    private static RunDescriptor Descriptor(DateTimeOffset at) => new()
    {
        Kind = ProbeKind.Icmp,
        ProbeName = "ping",
        Shape = ProbeResultShape.ScalarSeries,
        Target = Target.Ip("192.168.1.1"),
        Unit = MeasurementUnit.Milliseconds,
        Context = new MeasurementContext
        {
            InterfaceName = "Ethernet",
            AdapterKind = AdapterKind.Physical,
            InterfaceAddress = "192.168.1.10",
            CalibrationBaselineMs = 0.27,
            ProductVersion = "0.1.0",
            Methodology = Methodology.IcmpEcho,
            StartedUtc = at,
        },
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = SamplesPerRun,
        },
    };
}
