using Microsoft.Extensions.Logging;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Monitors;

/// <summary>Что планировщик сделал с монитором при старте.</summary>
/// <param name="Monitor">Монитор.</param>
/// <param name="Missed">Сколько сроков прошло мимо.</param>
/// <param name="Action">Решение словами — его и показываем оператору.</param>
public sealed record MisfireReport(Monitor Monitor, int Missed, string Action);

/// <summary>
/// Планировщик мониторов.
/// </summary>
/// <remarks>
/// Свой, а не Quartz.NET, вопреки решению R-15 в исследовании. Причина измерена,
/// а не выведена: спайк-06 показал, что Quartz с обрезкой публикации падает при первом
/// же обращении — <c>SimpleTypeLoadHelper</c> инстанцируется по имени типа, обрезчик
/// удаляет его конструктор, и <b>предупреждений при сборке не возникает ни одного</b>.
/// Библиотека, ломающаяся молча на машине пользователя, хуже собственного кода,
/// который делает ровно то, что нам нужно. Подробности — docs/02-research.md, R-15.
/// <para>
/// Из Quartz нам была нужна одна вещь: срок, переживающий выключение машины. Он лежит
/// в нашей же базе полем <see cref="Monitor.NextDueUtc"/> — и этого достаточно, потому
/// что после включения продукт видит срок в прошлом и знает, сколько именно пропущено.
/// </para>
/// <para>
/// Время берётся из <see cref="TimeProvider"/>, а не из <see cref="DateTimeOffset.UtcNow"/>.
/// Иначе поведение «машина спала восемь часов» нельзя было бы проверить тестом,
/// а именно оно и есть предмет приёмки.
/// </para>
/// </remarks>
public sealed class MonitorScheduler(
    IMonitorStore store,
    MonitorService service,
    TimeProvider time,
    ILogger<MonitorScheduler> logger) : IAsyncDisposable
{
    /// <summary>Как часто сверяются часы.</summary>
    /// <remarks>
    /// Секунда — не точность запуска, а её предел. Мониторы не бывают чаще, чем раз
    /// в полминуты, и секундной сетки для них с запасом достаточно.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>Как часто перечитывается список мониторов.</summary>
    /// <remarks>
    /// Список меняют и консоль, и графический клиент, работающие одновременно
    /// над одним файлом. Перечитывать его каждую секунду — тратить обращения к диску
    /// впустую; не перечитывать вовсе — не заметить монитор, заведённый соседом.
    /// </remarks>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    /// <summary>Сколько проверок идёт одновременно.</summary>
    /// <remarks>
    /// Ограничение существует ради самих измерений: десяток одновременных проб
    /// на одном адаптере мерили бы уже не сеть, а очередь к ней.
    /// </remarks>
    private const int MaxConcurrent = 3;

    private readonly IMonitorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly MonitorService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<MonitorScheduler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly HashSet<Guid> _running = [];
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private IReadOnlyList<Monitor> _cache = [];
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Сколько проверок идёт прямо сейчас.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    public event EventHandler<MonitorCheck>? Checked
    {
        add => _service.Checked += value;
        remove => _service.Checked -= value;
    }

    public event EventHandler<AlertEvent>? Alerted
    {
        add => _service.Alerted += value;
        remove => _service.Alerted -= value;
    }

    /// <summary>
    /// Разбирает сроки, оставшиеся с прошлого запуска, и возвращает отчёт.
    /// </summary>
    /// <remarks>
    /// Отчёт нужен человеку: продукт, молча проглотивший сотню пропущенных проверок,
    /// оставляет в истории необъяснимую дыру.
    /// </remarks>
    public async Task<IReadOnlyList<MisfireReport>> PlanAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var monitors = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var reports = new List<MisfireReport>();

        foreach (var monitor in monitors.Where(m => m.IsEnabled))
        {
            if (monitor.NextDueUtc is not { } due)
            {
                await _store
                    .SetNextDueAsync(monitor.Id, monitor.Schedule.NextAfter(now), cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            var missed = monitor.Schedule.MissedSlots(due, now);

            // Ноль или один — обычное опоздание планировщика на доли секунды.
            // Такое просто выполняется в ближайший тик и отчёта не заслуживает.
            if (missed <= 1)
            {
                continue;
            }

            await _service.RecordMissedAsync(monitor, due, missed, cancellationToken).ConfigureAwait(false);

            var action = monitor.Schedule.Misfire == MisfirePolicy.RunOnce
                ? "выполнить один раз сейчас"
                : "пропустить, ждать следующего срока";

            if (monitor.Schedule.Misfire == MisfirePolicy.Skip)
            {
                await _store
                    .SetNextDueAsync(monitor.Id, monitor.Schedule.NextAfter(now), cancellationToken)
                    .ConfigureAwait(false);
            }

            reports.Add(new MisfireReport(monitor, missed, action));

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Монитор {Monitor}: пропущено {Missed}, политика — {Action}.",
                    monitor.Name,
                    missed,
                    action);
            }
        }

        return reports;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        await PlanAsync(cancellationToken).ConfigureAwait(false);

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = LoopAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановка по запросу — не ошибка.
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
    }

    /// <summary>Заставляет перечитать список при следующем тике.</summary>
    public void Invalidate() => _refreshedAt = DateTimeOffset.MinValue;

    /// <summary>Проверка вне расписания. Срок при этом не сдвигается.</summary>
    public Task<MonitorCheck> RunNowAsync(Monitor monitor, CancellationToken cancellationToken = default) =>
        _service.CheckAsync(monitor, scheduled: null, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _slots.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка по запросу.
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (now - _refreshedAt >= RefreshEvery)
        {
            _cache = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            _refreshedAt = now;
        }

        foreach (var monitor in _cache)
        {
            if (!monitor.IsEnabled || monitor.NextDueUtc is not { } due || due > now)
            {
                continue;
            }

            lock (_gate)
            {
                // Проверка, не успевшая закончиться к следующему сроку, не запускается
                // второй раз. Иначе медленный монитор наплодил бы очередь самому себе
                // и мерил бы уже её, а не сеть.
                if (!_running.Add(monitor.Id))
                {
                    continue;
                }
            }

            // Второй барьер, и он в базе. Первый живёт в памяти этого экземпляра
            // и про соседний процесс не знает, а со службой мониторов (И-21) два
            // планировщика над одной базой — обычная конфигурация: служба наблюдает
            // всегда, клиент открывают, когда надо посмотреть. Без захвата оба увидели
            // бы один срок и оба выполнили бы проверку.
            var claimed = await _store
                .TryClaimDueAsync(monitor.Id, due, NextSlot(monitor, due, now), cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                lock (_gate)
                {
                    _running.Remove(monitor.Id);
                }

                continue;
            }

            _ = ExecuteAsync(monitor, due, cancellationToken);
        }
    }

    private async Task ExecuteAsync(Monitor monitor, DateTimeOffset due, CancellationToken cancellationToken)
    {
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _service.CheckAsync(monitor, due, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка планировщика.
        }
        catch (Exception ex)
        {
            // Сюда попадает только сбой самой записи: отказ измерения служба
            // превращает в вердикт и записывает сама.
            _logger.LogError(ex, "Монитор {Monitor}: сбой при выполнении проверки.", monitor.Name);
        }
        finally
        {
            Advance(monitor, due);

            lock (_gate)
            {
                _running.Remove(monitor.Id);
            }
        }
    }

    /// <summary>
    /// Следующий срок после наступившего.
    /// </summary>
    /// <remarks>
    /// Сетка отсчитывается от назначенного срока, а не от момента завершения: иначе
    /// «каждый день в 3:00» уползало бы на длительность проверки каждые сутки.
    /// Но и догонять пропущенное из-за затянувшейся проверки нельзя — поэтому сроки,
    /// оставшиеся позади, пролистываются до первого будущего.
    /// <para>
    /// Считается в одном месте и используется дважды: при захвате срока и при правке
    /// кэша после проверки. Два разных вычисления одного и того же разошлись бы —
    /// и разошлись бы незаметно, потому что оба дают правдоподобный момент.
    /// </para>
    /// </remarks>
    private static DateTimeOffset? NextSlot(Monitor monitor, DateTimeOffset due, DateTimeOffset now)
    {
        var next = monitor.Schedule.NextAfter(due);

        for (var guard = 0; guard < 10_000 && next is { } moment && moment <= now; guard++)
        {
            next = monitor.Schedule.NextAfter(moment);
        }

        return next;
    }

    /// <summary>
    /// Приводит кэш в соответствие с базой после проверки.
    /// </summary>
    /// <remarks>
    /// В базу писать уже нечего: срок сдвинут захватом <b>до</b> проверки, а не после
    /// неё. Порядок именно такой из-за второго планировщика — иначе оба успели бы
    /// увидеть срок наступившим, пока первый мерил.
    /// <para>
    /// Цена известна и принята: процесс, погибший посреди проверки, оставит срок уже
    /// сдвинутым, и эта одна проверка не запишется даже пропуском. Систематический
    /// двойной запуск обошёлся бы дороже — он удваивает измерения в журнале и заставляет
    /// правило оповещения считать два отказа там, где случился один.
    /// </para>
    /// </remarks>
    private void Advance(Monitor monitor, DateTimeOffset due)
    {
        var next = NextSlot(monitor, due, _time.GetUtcNow());

        // Кэш правится на месте: до следующего перечитывания монитор иначе
        // остался бы с прошедшим сроком и запустился бы снова через секунду.
        _cache = [.. _cache.Select(m => m.Id == monitor.Id ? m with { NextDueUtc = next } : m)];
    }
}
