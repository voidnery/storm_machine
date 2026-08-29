using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StormMachine.Application.Monitors;
using StormMachine.Application.Runs;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Два планировщика над одной базой не выполняют одну проверку дважды.
/// </summary>
/// <remarks>
/// До И-21 это была экзотика: «storm monitors watch» рядом с открытым окном. Со службой
/// мониторов такая пара стала <b>обычной</b> конфигурацией — служба наблюдает всегда,
/// клиент открывают, когда надо посмотреть.
/// <para>
/// Защита у планировщика была, но жила в памяти его экземпляра и про соседний процесс
/// ничего не знала. Два планировщика увидели бы один наступивший срок и оба выполнили бы
/// проверку: в журнал легли бы два прогона вместо одного, а правило оповещения посчитало
/// бы два отказа подряд там, где случился один, — и гистерезис сработал бы раньше времени.
/// </para>
/// </remarks>
public sealed class SchedulerClaimTests
{
    private static Monitor Every(TimeSpan interval, DateTimeOffset due) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Шлюз",
        Subject = "fake",
        Target = Target.Ip("192.168.1.1"),
        Schedule = Schedule.Every(interval),
        NextDueUtc = due,
    };

    private static MonitorScheduler Build(FakeMonitorStore store, FakeProbe probe, TimeProvider time)
    {
        var orchestrator = new RunOrchestrator(new NullRunStore(), new NullClock(), new NullEnvironment());
        var registry = new FakeRegistry(probe);

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

    /// <summary>
    /// Наступивший срок достаётся ровно одному.
    /// </summary>
    /// <remarks>
    /// Проверяется на хранилище, а не на двух процессах: предмет здесь — условие
    /// в запросе, а не операционная система. Второй захват того же срока обязан
    /// вернуть ложь, потому что срок уже сдвинут.
    /// </remarks>
    [Fact]
    public async Task DueSlot_IsClaimedByExactlyOne()
    {
        var store = new FakeMonitorStore();
        var due = DateTimeOffset.UnixEpoch;
        var monitor = Every(TimeSpan.FromMinutes(5), due);

        await store.SaveAsync(monitor);

        var first = await store.TryClaimDueAsync(monitor.Id, due, due.AddMinutes(5));
        var second = await store.TryClaimDueAsync(monitor.Id, due, due.AddMinutes(5));

        Assert.True(first, "Первый захват обязан удаться.");
        Assert.False(second, "Второй захват того же срока — это и есть двойной запуск.");
    }

    /// <summary>Захват чужого срока не проходит: сдвигать можно только то, что наблюдал.</summary>
    [Fact]
    public async Task ClaimingAStaleSlot_Fails()
    {
        var store = new FakeMonitorStore();
        var due = DateTimeOffset.UnixEpoch;
        var monitor = Every(TimeSpan.FromMinutes(5), due);

        await store.SaveAsync(monitor);

        var stale = await store.TryClaimDueAsync(monitor.Id, due.AddMinutes(-5), due.AddMinutes(5));

        Assert.False(stale);

        // Проигранный захват ничего не меняет: срок остался прежним.
        var stored = await store.GetAsync(monitor.Id);

        Assert.Equal(due, stored!.NextDueUtc);
    }

    /// <summary>
    /// Один планировщик выполняет наступивший срок один раз.
    /// </summary>
    /// <remarks>
    /// Основа для следующей проверки: сначала надо убедиться, что захват не сломал
    /// обычную работу, и только потом — что он ловит второго.
    /// </remarks>
    [Fact]
    public async Task SingleScheduler_RunsTheDueCheckOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeMonitorStore();
        var probe = new FakeProbe(() => 1.0);

        await store.SaveAsync(Every(TimeSpan.FromMinutes(5), time.GetUtcNow()));

        await using var scheduler = Build(store, probe, time);

        await scheduler.StartAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitForChecksAsync(store, 1);
        await scheduler.StopAsync();

        Assert.Equal(1, probe.Runs);
    }

    /// <summary>
    /// Два планировщика над одной базой выполняют её один раз на двоих.
    /// </summary>
    /// <remarks>
    /// Это и есть регрессия на дефект, который служба мониторов сделала бы штатным.
    /// Оба планировщика видят один и тот же наступивший срок — забрать его должен один.
    /// </remarks>
    [Fact]
    public async Task TwoSchedulers_RunTheDueCheckOnceBetweenThem()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeMonitorStore();
        var probe = new FakeProbe(() => 1.0);

        // Срок ставится в будущее намеренно — чтобы оба успели прочитать список,
        // пока он ещё не наступил.
        await store.SaveAsync(Every(TimeSpan.FromMinutes(5), time.GetUtcNow().AddSeconds(3)));

        // Служба и открытый клиент: разные экземпляры, одна база.
        await using var service = Build(store, probe, time);
        await using var client = Build(store, probe, time);

        await service.StartAsync();
        await client.StartAsync();

        // Первый тик: оба перечитывают список и видят срок будущим. Ни один не заявляет
        // прав — заявлять пока не на что.
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(0, store.Claims);

        // Второй тик наступает раньше, чем истечёт срок жизни кэша (пять секунд),
        // поэтому список никто не перечитывает: оба судят по своей копии, и в обеих
        // копиях срок теперь наступил. Это и есть настоящая гонка, а не её имитация:
        // ровно так же разойдутся служба и клиент в разных процессах.
        time.Advance(TimeSpan.FromSeconds(3));
        await WaitForChecksAsync(store, 1);

        await service.StopAsync();
        await client.StopAsync();

        Assert.True(
            store.Claims > 1,
            $"Захват пробовали {store.Claims} раз — гонки не было, и проверка ничего не доказывает.");

        Assert.Equal(1, probe.Runs);
        Assert.Single(store.Checks);
    }

    /// <summary>
    /// Срок сдвигается до проверки, а не после.
    /// </summary>
    /// <remarks>
    /// Порядок существенный: пока идёт измерение, соседний планировщик обязан видеть
    /// срок уже занятым. Сдвиг после проверки оставлял бы окно на всю её длительность —
    /// а проверка бывает долгой.
    /// </remarks>
    [Fact]
    public async Task DueMovesForward_BeforeTheCheckRuns()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeMonitorStore();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var probe = new FakeProbe(() =>
        {
            started.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(5));

            return 1.0;
        });

        var monitor = Every(TimeSpan.FromMinutes(5), time.GetUtcNow());
        await store.SaveAsync(monitor);

        await using var scheduler = Build(store, probe, time);

        await scheduler.StartAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Проверка идёт прямо сейчас — а срок в базе уже будущий.
        var during = await store.GetAsync(monitor.Id);

        Assert.NotNull(during!.NextDueUtc);
        Assert.True(
            during.NextDueUtc > DateTimeOffset.UnixEpoch,
            "Пока идёт проверка, срок остался прошедшим — соседний планировщик запустит её снова.");

        release.TrySetResult();
        await scheduler.StopAsync();
    }

    private static async Task WaitForChecksAsync(FakeMonitorStore store, int count)
    {
        for (var attempt = 0; attempt < 100 && store.Checks.Count < count; attempt++)
        {
            await Task.Delay(20);
        }
    }
}
