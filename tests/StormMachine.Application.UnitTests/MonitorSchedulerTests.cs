using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Application.Runs;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Планировщик: сроки, пропуски и то, что расписание переживает перезапуск.
/// </summary>
/// <remarks>
/// Первая половина приёмки И-14. Часы управляемые, поэтому «машина спала восемь часов»
/// проверяется за миллисекунды, а не за восемь часов — и, что важнее, проверяется
/// каждый раз, а не однажды руками.
/// </remarks>
public sealed class MonitorSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(double value = 10)
        {
            Time = new FakeTimeProvider(Start);
            Probe = new FakeProbe(() => value);

            var orchestrator = new RunOrchestrator(new NullRunStore(), new NullClock(), new NullEnvironment());
            var registry = new FakeRegistry(Probe);

            Service = new MonitorService(
                Store,
                registry,
                orchestrator,
                new ScenarioRunner(registry, orchestrator),
                [Channel],
                Time,
                NullLogger<MonitorService>.Instance);

            Scheduler = new MonitorScheduler(Store, Service, Time, NullLogger<MonitorScheduler>.Instance);
        }

        public FakeTimeProvider Time { get; }

        public FakeMonitorStore Store { get; } = new();

        public RecordingChannel Channel { get; } = new();

        public FakeProbe Probe { get; }

        public MonitorService Service { get; }

        public MonitorScheduler Scheduler { get; }

        public async Task<Monitor> AddAsync(Monitor monitor)
        {
            var planned = monitor with { NextDueUtc = monitor.Schedule.NextAfter(Time.GetUtcNow()) };

            await Store.SaveAsync(planned);

            return planned;
        }

        /// <summary>Двигает часы и даёт планировщику доработать запущенные проверки.</summary>
        public async Task AdvanceAsync(TimeSpan span)
        {
            Time.Advance(span);

            // Проверки выполняются фоновыми задачами; часы их не ждут. Небольшая
            // настоящая пауза даёт им завершиться до того, как тест смотрит итог.
            for (var i = 0; i < 50 && Scheduler.ActiveCount > 0; i++)
            {
                await Task.Delay(10);
            }

            await Task.Delay(20);
        }

        public async ValueTask DisposeAsync() => await Scheduler.DisposeAsync();
    }

    // --------------------------------------------------------------- обычный ход

    [Fact(DisplayName = "Монитор выполняется в назначенный срок")]
    public async Task RunsWhenDue()
    {
        await using var harness = new Harness();

        await harness.AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(1))));
        await harness.Scheduler.StartAsync();

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(harness.Store.Checks);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Single(harness.Store.Checks);
    }

    [Fact(DisplayName = "Следующий срок отсчитывается от сетки, а не от конца проверки")]
    public async Task GridDoesNotDrift()
    {
        await using var harness = new Harness();

        var monitor = await harness
            .AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(1))))
            ;

        await harness.Scheduler.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        var stored = await harness.Store.GetAsync(monitor.Id);

        // Первый срок был 12:01, следующий обязан быть 12:02 — независимо от того,
        // сколько длилась проверка. Иначе суточный монитор уползал бы каждый день.
        Assert.Equal(Start.AddMinutes(2), stored!.NextDueUtc);
    }

    [Fact(DisplayName = "Выключенный монитор не запускается")]
    public async Task DisabledDoesNotRun()
    {
        await using var harness = new Harness();

        await harness
            .AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(1))) with { IsEnabled = false })
            ;

        await harness.Scheduler.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Empty(harness.Store.Checks);
    }

    // ------------------------------------------------------------------ пропуски

    [Fact(DisplayName = "После сна с политикой «пропустить» назначается новый срок")]
    public async Task SleepWithSkip()
    {
        await using var harness = new Harness();

        var monitor = await harness
            .AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(5), MisfirePolicy.Skip)))
            ;

        // Монитор заведён в 12:00, первый срок — 12:05. Машина спала восемь часов
        // и проснулась в 20:00: между 12:05 и 20:00 умещается 95 пятиминутных сроков.
        harness.Time.Advance(TimeSpan.FromHours(8));

        var reports = await harness.Scheduler.PlanAsync();

        Assert.Single(reports);
        Assert.Equal(95, reports[0].Missed);

        // Пропущенное записано одной строкой — не сотней пустых.
        var missed = Assert.Single(harness.Store.Checks);
        Assert.Equal(CheckKind.Missed, missed.Kind);
        Assert.Equal(95, missed.MissedCount);

        var stored = await harness.Store.GetAsync(monitor.Id);

        Assert.True(stored!.NextDueUtc > harness.Time.GetUtcNow(), "срок остался в прошлом");
    }

    [Fact(DisplayName = "После сна с политикой «выполнить» проверка идёт сразу")]
    public async Task SleepWithCatchUp()
    {
        await using var harness = new Harness();

        await harness
            .AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(5), MisfirePolicy.RunOnce)))
            ;

        harness.Time.Advance(TimeSpan.FromHours(8));

        await harness.Scheduler.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));

        // Одна запись про пропуск и ровно одна выполненная проверка: наверстать
        // девяносто пять замеров залпом означало бы мерить очередь к адаптеру, а не сеть.
        Assert.Equal(1, harness.Store.Checks.Count(c => c.Kind == CheckKind.Missed));
        Assert.Equal(1, harness.Store.Checks.Count(c => c.Kind == CheckKind.Measured));
        Assert.Equal(1, harness.Probe.Runs);
    }

    [Fact(DisplayName = "Опоздание на секунды пропуском не считается и в историю не идёт")]
    public async Task SmallDelayLeavesNoTrace()
    {
        await using var harness = new Harness();

        await harness
            .AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(5))))
            ;

        harness.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(3));

        Assert.Empty(await harness.Scheduler.PlanAsync());
        Assert.Empty(harness.Store.Checks);
    }

    // -------------------------------------------------------------- перезапуск

    [Fact(DisplayName = "Расписание переживает перезапуск: срок читается из хранилища")]
    public async Task SurvivesRestart()
    {
        var time = new FakeTimeProvider(Start);
        var store = new FakeMonitorStore();
        var probe = new FakeProbe(() => 10);
        var orchestrator = new RunOrchestrator(new NullRunStore(), new NullClock(), new NullEnvironment());
        var registry = new FakeRegistry(probe);

        MonitorScheduler Build()
        {
            var service = new MonitorService(
                store,
                registry,
                orchestrator,
                new ScenarioRunner(registry, orchestrator),
                [],
                time,
                NullLogger<MonitorService>.Instance);

            return new MonitorScheduler(store, service, time, NullLogger<MonitorScheduler>.Instance);
        }

        var monitor = Fakes.Monitor(Schedule.ByCron("0 3 * * *"));

        await store.SaveAsync(monitor with { NextDueUtc = monitor.Schedule.NextAfter(Start) });

        var before = (await store.GetAsync(monitor.Id))!.NextDueUtc;

        // Продукт закрыли и открыли: новый планировщик над тем же хранилищем.
        await using (var first = Build())
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        await using var second = Build();
        await second.PlanAsync();

        var after = (await store.GetAsync(monitor.Id))!.NextDueUtc;

        // Срок не пересчитан от «сейчас» и не потерян: он лежал в базе и лежит там же.
        Assert.Equal(before, after);
        Assert.NotNull(after);
    }

    // ---------------------------------------------------------------- алерты

    [Fact(DisplayName = "Алерт поднимается через две проверки и уходит в канал")]
    public async Task AlertReachesChannel()
    {
        // Проба отдаёт 500 при пороге 100: каждая проверка — нарушение.
        await using var harness = new Harness(value: 500);

        await harness.AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(1))) with
        {
            Alert = new AlertRule { Cooldown = TimeSpan.Zero, Channels = ["тест"] },
        });

        await harness.Scheduler.StartAsync();

        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(harness.Channel.Sent);

        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        var sent = Assert.Single(harness.Channel.Sent);

        Assert.Equal(AlertAction.Raised, sent.Event.Action);
        Assert.Equal(VerdictLevel.Fail, sent.Check.Level);

        // Событие попало и в ленту, а не только в канал.
        Assert.Single(harness.Store.Alerts);
    }

    [Fact(DisplayName = "Ненастроенный канал объясняет, чего ему не хватает")]
    public async Task UnconfiguredChannelIsReported()
    {
        await using var harness = new Harness(value: 500);

        await harness.AddAsync(Fakes.Monitor(Schedule.Every(TimeSpan.FromMinutes(1))) with
        {
            Alert = new AlertRule { RaiseAfter = 1, Cooldown = TimeSpan.Zero, Channels = ["почта"] },
        });

        await harness.Scheduler.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        var alert = Assert.Single(harness.Store.Alerts);

        // Молчащий канал опаснее отсутствующего: на него рассчитывают.
        Assert.Empty(alert.Channels);
        Assert.Single(alert.DeliveryErrors);
        Assert.Contains("не зарегистрирован", alert.DeliveryErrors[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ обслуживание

    [Fact(DisplayName = "В окне обслуживания проверка не выполняется, но след остаётся")]
    public async Task MaintenanceLeavesATrace()
    {
        await using var harness = new Harness();

        var schedule = Schedule.Every(TimeSpan.FromMinutes(1)) with
        {
            Maintenance =
            [
                new MaintenanceWindow
                {
                    Start = new TimeOnly(0, 0),
                    End = new TimeOnly(23, 59),
                    Reason = "стенд",
                },
            ],
        };

        // Срок назначается вручную: расписание целиком накрыто окном, и сам
        // планировщик такого срока не назначил бы.
        await harness.Store.SaveAsync(
            Fakes.Monitor(schedule) with { NextDueUtc = Start.AddSeconds(30) });

        await harness.Scheduler.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        var check = Assert.Single(harness.Store.Checks);

        Assert.Equal(CheckKind.Maintenance, check.Kind);
        Assert.Equal(0, harness.Probe.Runs);
        Assert.Contains("стенд", check.Summary, StringComparison.Ordinal);
    }
}
