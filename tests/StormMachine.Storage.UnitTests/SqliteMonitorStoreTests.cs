using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Хранилище мониторов.
/// </summary>
/// <remarks>
/// Главное свойство, которое здесь закрепляется, — расписание переживает закрытие
/// продукта. Проверяется буквально: запись, закрытие соединений, открытие нового
/// хранилища над тем же файлом. Без этого «расписание переживает перезапуск»
/// оставалось бы утверждением про код, а не про файл на диске.
/// </remarks>
public sealed class SqliteMonitorStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteMonitorStoreTests()
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

    private SqliteMonitorStore CreateStore() =>
        new(new SqliteRunStore(new StorageOptions { DatabasePath = _databasePath }));

    private static Monitor Sample(string name = "шлюз") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "доступность шлюза",
        Subject = "ping",
        Target = Target.Ip("192.168.1.1", "шлюз"),
        Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = "10",
            ["interval"] = "200",
        },
        Thresholds = [Threshold.Parse("p95 < 50"), Threshold.Parse("loss < 1", VerdictLevel.Warn)],
        Schedule = Schedule.Every(TimeSpan.FromMinutes(5), MisfirePolicy.RunOnce) with
        {
            Maintenance =
            [
                new MaintenanceWindow
                {
                    Days = [DayOfWeek.Sunday],
                    Start = new TimeOnly(2, 0),
                    End = new TimeOnly(4, 0),
                    Reason = "работы у провайдера",
                },
            ],
        },
        Alert = new AlertRule
        {
            RaiseAfter = 3,
            ClearAfter = 2,
            ClearMargin = 10,
            Cooldown = TimeSpan.FromMinutes(20),
            RepeatEvery = TimeSpan.FromHours(1),
            Channels = ["webhook", "почта"],
        },
        Objective = new ServiceLevelObjective { TargetPercent = 99.5, Window = TimeSpan.FromDays(30) },
        NextDueUtc = Noon,
    };

    [Fact(DisplayName = "Монитор возвращается из базы таким же, каким его записали")]
    public async Task RoundTrip()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);

        var loaded = await store.GetAsync(monitor.Id);

        Assert.NotNull(loaded);
        Assert.Equal(monitor.Name, loaded!.Name);
        Assert.Equal(monitor.Target.Value, loaded.Target.Value);
        Assert.Equal(monitor.Target.Label, loaded.Target.Label);
        Assert.Equal("10", loaded.Parameters["count"]);
        Assert.Equal(2, loaded.Thresholds.Count);
        Assert.Equal(VerdictLevel.Warn, loaded.Thresholds[1].Level);
        Assert.Equal(monitor.NextDueUtc, loaded.NextDueUtc);
    }

    [Fact(DisplayName = "Расписание со всеми подробностями переживает запись и чтение")]
    public async Task ScheduleRoundTrip()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);

        var schedule = (await store.GetAsync(monitor.Id))!.Schedule;

        Assert.Equal(ScheduleKind.Every, schedule.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.Interval);
        Assert.Equal(MisfirePolicy.RunOnce, schedule.Misfire);

        var window = Assert.Single(schedule.Maintenance);

        Assert.Equal([DayOfWeek.Sunday], window.Days);
        Assert.Equal(new TimeOnly(2, 0), window.Start);
        Assert.Equal("работы у провайдера", window.Reason);
    }

    [Fact(DisplayName = "Правило алерта и цель SLA переживают запись и чтение")]
    public async Task AlertRoundTrip()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);

        var loaded = (await store.GetAsync(monitor.Id))!;

        Assert.NotNull(loaded.Alert);
        Assert.Equal(3, loaded.Alert!.RaiseAfter);
        Assert.Equal(10, loaded.Alert.ClearMargin);
        Assert.Equal(TimeSpan.FromMinutes(20), loaded.Alert.Cooldown);
        Assert.Equal(TimeSpan.FromHours(1), loaded.Alert.RepeatEvery);
        Assert.Equal(["webhook", "почта"], loaded.Alert.Channels);

        Assert.NotNull(loaded.Objective);
        Assert.Equal(99.5, loaded.Objective!.TargetPercent);
        Assert.Equal(TimeSpan.FromDays(30), loaded.Objective.Window);
    }

    [Fact(DisplayName = "Cron сохраняется строкой, как её написал человек")]
    public async Task CronKeepsText()
    {
        var store = CreateStore();
        var monitor = Sample() with { Schedule = Schedule.ByCron("0 3 * * MON-FRI") };

        await store.SaveAsync(monitor);

        Assert.Equal("0 3 * * MON-FRI", (await store.GetAsync(monitor.Id))!.Schedule.Cron);
    }

    [Fact(DisplayName = "Назначенный срок переживает закрытие продукта")]
    public async Task NextDueSurvivesRestart()
    {
        var monitor = Sample();

        await CreateStore().SaveAsync(monitor);

        // Продукт закрыли: соединения освобождены, объекты выброшены.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Продукт открыли заново — другое хранилище, тот же файл.
        var loaded = await CreateStore().GetAsync(monitor.Id);

        Assert.Equal(Noon, loaded!.NextDueUtc);
    }

    [Fact(DisplayName = "Срок переписывается отдельно, не трогая определение")]
    public async Task SetNextDueDoesNotTouchDefinition()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);
        await store.SetNextDueAsync(monitor.Id, Noon.AddHours(1));

        var loaded = (await store.GetAsync(monitor.Id))!;

        Assert.Equal(Noon.AddHours(1), loaded.NextDueUtc);

        // «Изменён» относится к тому, что задал человек. Пересчёт срока сам по себе
        // редактированием монитора не является.
        Assert.Equal(monitor.UpdatedUtc.UtcTicks, loaded.UpdatedUtc.UtcTicks);
    }

    [Fact(DisplayName = "Состояние алерта переживает перезапуск — счётчики не обнуляются")]
    public async Task AlertStateSurvives()
    {
        var monitor = Sample();
        var store = CreateStore();

        await store.SaveAsync(monitor);
        await store.SaveStatusAsync(
            monitor.Id,
            new MonitorStatus
            {
                Level = VerdictLevel.Fail,
                LastRunUtc = Noon,
                LastSummary = "цель не отвечает",
                Alert = new AlertState { IsRaised = true, Bad = 4, RaisedUtc = Noon, LastNotifiedUtc = Noon },
            });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var status = await CreateStore().GetStatusAsync(monitor.Id);

        // Иначе перезапуск продукта поднимал бы уже поднятый алерт заново
        // и слал повторное оповещение о том, о чём уже сообщили.
        Assert.True(status.Alert.IsRaised);
        Assert.Equal(4, status.Alert.Bad);
        Assert.Equal(Noon, status.Alert.RaisedUtc);
        Assert.Equal(VerdictLevel.Fail, status.Level);
    }

    [Fact(DisplayName = "Проверки пишутся и читаются в обратном хронологическом порядке")]
    public async Task ChecksRoundTrip()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);

        for (var i = 0; i < 5; i++)
        {
            await store.AppendCheckAsync(new MonitorCheck
            {
                Id = Guid.NewGuid(),
                MonitorId = monitor.Id,
                StartedUtc = Noon.AddMinutes(i),
                Duration = TimeSpan.FromSeconds(2),
                Kind = i == 3 ? CheckKind.Missed : CheckKind.Measured,
                Level = i == 1 ? VerdictLevel.Fail : VerdictLevel.Pass,
                Summary = $"проверка {i}",
                Metric = "p95",
                Value = 12.5 + i,
                Threshold = 50,
                MissedCount = i == 3 ? 7 : 0,
            });
        }

        var checks = await store.ListChecksAsync(new CheckQuery { MonitorId = monitor.Id });

        Assert.Equal(5, checks.Count);
        Assert.Equal(Noon.AddMinutes(4), checks[0].StartedUtc);
        Assert.Equal(CheckKind.Missed, checks.Single(c => c.MissedCount == 7).Kind);
        Assert.Equal(12.5, checks.Single(c => c.Summary == "проверка 0").Value);
    }

    [Fact(DisplayName = "Удаление монитора убирает его проверки")]
    public async Task DeleteCascadesChecks()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);
        await store.AppendCheckAsync(new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = Noon,
            Summary = "проверка",
        });

        Assert.True(await store.DeleteAsync(monitor.Id));

        Assert.Empty(await store.ListChecksAsync(new CheckQuery { MonitorId = monitor.Id }));
    }

    [Fact(DisplayName = "События алертов переживают удаление монитора")]
    public async Task AlertsOutliveTheMonitor()
    {
        var store = CreateStore();
        var monitor = Sample();

        await store.SaveAsync(monitor);
        await store.AppendAlertAsync(new AlertEvent
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            MonitorName = monitor.Name,
            AtUtc = Noon,
            Action = AlertAction.Raised,
            Level = VerdictLevel.Fail,
            Reason = "две проверки подряд не прошли",
            Summary = "цель не отвечает",
            Notified = true,
            Channels = ["webhook"],
            DeliveryErrors = ["почта: сервер отверг письмо"],
        });

        await store.DeleteAsync(monitor.Id);

        // Монитор могли убрать именно потому, что он сработал. Факт срабатывания
        // от этого не перестаёт быть фактом, и имя в событии продублировано,
        // чтобы оно читалось без монитора.
        var alert = Assert.Single(await store.ListAlertsAsync(new AlertQuery()));

        Assert.Equal("шлюз", alert.MonitorName);
        Assert.Equal(["webhook"], alert.Channels);
        Assert.Single(alert.DeliveryErrors);
    }

    [Fact(DisplayName = "Поиск принимает имя, его начало и начало идентификатора")]
    public async Task FindsByNameAndId()
    {
        var store = CreateStore();
        var monitor = Sample("шлюз офиса");

        await store.SaveAsync(monitor);

        Assert.NotNull(await store.FindAsync("шлюз офиса"));
        Assert.NotNull(await store.FindAsync("шлюз"));
        Assert.NotNull(await store.FindAsync(monitor.Id.ToString()[..8]));
        Assert.Null(await store.FindAsync("маршрутизатор"));
    }

    [Fact(DisplayName = "Неоднозначное сокращение — ошибка, а не догадка")]
    public async Task AmbiguousNameIsAnError()
    {
        var store = CreateStore();

        await store.SaveAsync(Sample("шлюз офиса"));
        await store.SaveAsync(Sample("шлюз склада"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("шлюз"));
    }

    [Fact(DisplayName = "Точное имя выигрывает у совпадения по началу")]
    public async Task ExactNameWins()
    {
        var store = CreateStore();

        await store.SaveAsync(Sample("шлюз"));
        await store.SaveAsync(Sample("шлюз офиса"));

        // Иначе монитор, названный ровно так, стало бы невозможно открыть
        // после появления соседа с более длинным именем.
        Assert.Equal("шлюз", (await store.FindAsync("шлюз"))!.Name);
    }
}
