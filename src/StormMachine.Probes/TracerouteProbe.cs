using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Проба traceroute и непрерывный MTR: маршрут до цели с задержкой и потерями на каждом хопе.
/// </summary>
/// <remarks>
/// Даёт результат, которого исходная модель не предусматривала вовсе: не ряд, а
/// <b>матрицу «хоп × попытка»</b>. Потери и задержку нужно считать по каждому хопу
/// отдельно, поэтому в <see cref="Sample"/> появилось поле <see cref="Sample.Group"/>.
/// <para>
/// Сделано через <c>IcmpSendEcho2</c>, а не через raw-сокеты. На этапе исследования
/// raw-вариант был проверен и отвергнут: TTL Exceeded до сокета не доходил, и все
/// промежуточные хопы терялись, тогда как системный API возвращает их корректно
/// (docs/02-research.md, R-01).
/// </para>
/// <para>
/// В И-7 добавлен непрерывный режим (<c>rounds</c>). Разовая трассировка отвечает
/// на вопрос «каким путём», непрерывная — на вопрос «где рвётся», а он и есть настоящий:
/// проблему, которая случается раз в минуту, разовым запуском не поймать.
/// </para>
/// </remarks>
public sealed class TracerouteProbe(
    IHighResolutionClock clock,
    TargetResolver resolver,
    IHopAnnotator? annotator = null) : IProbe
{
    /// <summary>Адрес-заглушка, который системный API возвращает вместо «неизвестно».</summary>
    private const string UnknownAddress = "0.0.0.0";

    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TargetResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IHopAnnotator? _annotator = annotator;

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Traceroute,
        Shape = ProbeResultShape.PathTrace,
        Name = "trace",
        Title = "Traceroute и MTR",
        Description = "Маршрут до цели: задержка и потери на каждом хопе.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.Traceroute,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "max-hops", Label = "Предел хопов", Type = ProbeParameterType.Integer,
                DefaultValue = 30, Minimum = 1, Maximum = 255,
                Description = "На каком TTL остановиться, если цель не достигнута.",
            },
            new ProbeParameter
            {
                Name = "attempts", Label = "Попыток на хоп при разведке", Type = ProbeParameterType.Integer,
                DefaultValue = 3, Minimum = 1, Maximum = 100,
                Description = "Сколько пакетов отправить с каждым TTL на первом проходе.",
            },
            new ProbeParameter
            {
                Name = "rounds", Label = "Циклов наблюдения", Type = ProbeParameterType.Integer,
                DefaultValue = 1, Minimum = 1, Maximum = 100_000,
                Description = "1 — разовая трассировка. Больше — непрерывный MTR: "
                              + "каждый цикл шлёт по одному пакету на хоп.",
            },
            new ProbeParameter
            {
                Name = "interval", Label = "Интервал цикла, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 1000, Minimum = 100, Maximum = 600_000,
                Description = "Через сколько начинать следующий цикл наблюдения.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Ждать ответа, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 2000, Minimum = 1, Maximum = 60_000,
                Description = "Сколько ждать ответа от хопа.",
            },
            new ProbeParameter
            {
                Name = "size", Label = "Размер данных, байт", Type = ProbeParameterType.Integer,
                DefaultValue = 32, Minimum = 0, Maximum = 65_500,
                Description = "Полезная нагрузка без заголовков.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request) =>
        ProbeValidation.Validate(Descriptor, request);

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var maxHops = request.GetParameter("max-hops", 30);
        var attempts = request.GetParameter("attempts", 3);
        var rounds = request.GetParameter("rounds", 1);
        var intervalMs = request.GetParameter("interval", 1000);
        var timeoutMs = request.GetParameter("timeout", 2000);
        var payloadBytes = request.GetParameter("size", 32);

        var destination = await _resolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var destinationAddress = destination.ToString();
        observer.OnResolved(destinationAddress);

        var buffer = new byte[payloadBytes];
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var state = new TraceState(destinationAddress);

        // По каналу на хоп: один объект Ping не поддерживает одновременных операций,
        // а цикл наблюдения обязан опрашивать хопы параллельно. Каналы переиспользуются
        // между циклами — иначе за час непрерывного MTR их набежали бы сотни тысяч.
        var channels = new Dictionary<int, HopChannel>();
        var cancelled = false;

        try
        {
            using (var warmUp = new Ping())
            {
                // Прогрев асинхронного пути — та же причина, что и в ICMP-пробе: первый вызов
                // тянет за собой компиляцию и завышает измерение (см. И-1).
                try
                {
                    await warmUp
                        .SendPingAsync(IPAddress.Loopback, TimeSpan.FromMilliseconds(500), buffer, null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            // Проход разведки: последовательно, с ранним выходом. Параллелить его нельзя —
            // длина маршрута ещё неизвестна, и параллельный запуск разослал бы пакеты
            // на все 30 значений TTL там, где цель стоит восьмой.
            for (var ttl = 1; ttl <= maxHops && !state.Reached && !cancelled; ttl++)
            {
                var channel = Rent(channels, ttl, buffer);

                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    HopProbe probe = default;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        probe = await ProbeHopAsync(channel, destination, ttl, timeout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }

                    if (cancelled)
                    {
                        break;
                    }

                    yield return state.Accept(probe);
                }

                if (!cancelled)
                {
                    state.PathLength = ttl;
                }
            }

            for (var round = 1; round < rounds && !cancelled; round++)
            {
                var roundStartedAt = _clock.GetTimestamp();
                HopProbe[] results = [];

                try
                {
                    results = await ProbeRoundAsync(channels, destination, state.PathLength, timeout, buffer, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                if (cancelled)
                {
                    break;
                }

                var reachedThisRound = false;

                foreach (var probe in results)
                {
                    reachedThisRound |= probe.Reached;
                    yield return state.Accept(probe);
                }

                ExtendPathIfLengthened(state, results, reachedThisRound, maxHops);

                try
                {
                    await ProbePacing.WaitUntilNextAsync(_clock, roundStartedAt, intervalMs, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }
        }
        finally
        {
            foreach (var channel in channels.Values)
            {
                channel.Ping.Dispose();
            }
        }

        // Итог подводится и после отмены. Непрерывный MTR останавливают вручную —
        // это штатный способ его закончить, и терять из-за него имена узлов
        // и вывод о точке деградации значило бы обесценить час наблюдения.
        await ReportAsync(observer, state, maxHops, rounds, cancelled).ConfigureAwait(false);

        // Отмена сообщается наверх после подведения итога: оркестратор отличает
        // прерванный прогон от завершённого именно по этому исключению.
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Опрашивает все известные хопы одним циклом, по одному пакету на хоп.</summary>
    /// <remarks>
    /// Параллельно — иначе цикл не уложится в интервал: тридцать хопов подряд с таймаутом
    /// в две секунды дают минуту на цикл там, где нужна секунда. Каждая проба при этом
    /// по-прежнему замеряется своим таймером, поэтому параллельность не подменяет измерение.
    /// </remarks>
    private async Task<HopProbe[]> ProbeRoundAsync(
        Dictionary<int, HopChannel> channels,
        IPAddress destination,
        int pathLength,
        TimeSpan timeout,
        byte[] template,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<HopProbe>[pathLength];

        for (var ttl = 1; ttl <= pathLength; ttl++)
        {
            tasks[ttl - 1] = ProbeHopAsync(Rent(channels, ttl, template), destination, ttl, timeout, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<HopProbe> ProbeHopAsync(
        HopChannel channel,
        IPAddress destination,
        int ttl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var options = new PingOptions(ttl, false);
        var startedAt = _clock.GetTimestamp();
        var timestampUtc = DateTimeOffset.UtcNow;

        try
        {
            var reply = await channel.Ping
                .SendPingAsync(destination, timeout, channel.Payload, options, cancellationToken)
                .ConfigureAwait(false);

            var elapsed = _clock.ElapsedMilliseconds(startedAt);
            var responder = reply.Address?.ToString();

            if (string.IsNullOrEmpty(responder) || string.Equals(responder, UnknownAddress, StringComparison.Ordinal))
            {
                responder = null;
            }

            return reply.Status switch
            {
                // Ответ цели засчитывается, только если она вернула наши данные.
                // Чужой ответ — это не наш пакет, и наш при этом не вернулся:
                // для этого хопа честнее всего считать, что ответа не было.
                IPStatus.Success => channel.Matches(reply.Buffer)
                    ? new HopProbe(ttl, timestampUtc, elapsed, SampleStatus.Success, responder, Reached: true)
                    : HopProbe.Failed(ttl, timestampUtc, SampleStatus.Timeout),

                IPStatus.TtlExpired or IPStatus.TimeExceeded =>
                    new HopProbe(ttl, timestampUtc, elapsed, SampleStatus.Success, responder, Reached: false),

                IPStatus.TimedOut => HopProbe.Failed(ttl, timestampUtc, SampleStatus.Timeout),
                _ => HopProbe.Failed(ttl, timestampUtc, SampleStatus.Unreachable),
            };
        }
        catch (PingException)
        {
            return HopProbe.Failed(ttl, timestampUtc, SampleStatus.Error);
        }
    }

    /// <summary>
    /// Удлиняет наблюдаемый участок, если маршрут стал длиннее.
    /// </summary>
    /// <remarks>
    /// Признак удлинения — цель не достигнута, а последний известный хоп при этом ответил
    /// и ответил не целью. Просто пропавший пакет так не выглядит, и перескан из-за него
    /// не запускается: иначе непрерывный MTR к фильтрующему узлу каждый цикл обшаривал бы
    /// все 30 значений TTL.
    /// <para>
    /// Растёт по одному хопу за цикл. Медленно — зато без отдельного режима перескана
    /// и без всплеска трафика ровно в тот момент, когда с маршрутом и так что-то не так.
    /// </para>
    /// </remarks>
    private static void ExtendPathIfLengthened(TraceState state, HopProbe[] round, bool reachedThisRound, int maxHops)
    {
        if (reachedThisRound || state.PathLength >= maxHops || round.Length == 0)
        {
            return;
        }

        var last = round[^1];

        if (last.Responder is { } responder && !string.Equals(responder, state.DestinationAddress, StringComparison.Ordinal))
        {
            state.PathLength++;
        }
    }

    /// <summary>
    /// Канал одного хопа: свой объект <see cref="Ping"/> и свой помеченный пакет.
    /// </summary>
    /// <remarks>
    /// Метка в полезной нагрузке — защита от чужого ответа. При параллельном опросе
    /// всех хопов в сторону одной цели Windows иногда отдаёт эхо-ответ не тому
    /// ожидающему запросу: в непрерывном MTR к 8.8.8.8 это дало около процента
    /// ответов цели на низких TTL — фантомные строки посреди маршрута.
    /// <para>
    /// Эхо-ответ по RFC 792 повторяет отправленные данные, поэтому подмена ловится
    /// сравнением первых байт. Настоящее укорочение маршрута метку прошло бы: там
    /// цели достигает именно наш пакет, с нашей меткой, — значит фильтр не глушит
    /// сигнал, а отсеивает только путаницу.
    /// </para>
    /// </remarks>
    private sealed record HopChannel(Ping Ping, byte[] Payload)
    {
        /// <summary>Метка занимает два первых байта; при меньшем размере её просто нет.</summary>
        public const int TagLength = 2;

        public bool IsTagged => Payload.Length >= TagLength;

        public bool Matches(byte[]? echoed) =>
            !IsTagged
            || (echoed is not null
                && echoed.Length >= TagLength
                && echoed[0] == Payload[0]
                && echoed[1] == Payload[1]);
    }

    private static HopChannel Rent(Dictionary<int, HopChannel> channels, int ttl, byte[] template)
    {
        if (channels.TryGetValue(ttl, out var channel))
        {
            return channel;
        }

        var payload = (byte[])template.Clone();

        if (payload.Length >= HopChannel.TagLength)
        {
            payload[0] = (byte)ttl;
            payload[1] = (byte)(ttl ^ 0xFF);
        }

        channel = new HopChannel(new Ping(), payload);
        channels[ttl] = channel;

        return channel;
    }

    /// <summary>Сколько отводится на обогащение узлов после того, как измерение закончено.</summary>
    private static readonly TimeSpan AnnotationBudget = TimeSpan.FromSeconds(10);

    private async Task ReportAsync(
        IProbeObserver observer,
        TraceState state,
        int maxHops,
        int rounds,
        bool cancelled)
    {
        ReportSummary(observer, state, maxHops, rounds, cancelled);

        if (_annotator is null || state.Responders.Count == 0)
        {
            return;
        }

        // Собственный токен, а не токен прогона: обогащение идёт уже после измерения,
        // и отмена, которой его остановили, не должна заодно стереть имена узлов.
        // Бюджет нужен, чтобы медленный резолвер не подвесил подведение итога.
        using var budget = new CancellationTokenSource(AnnotationBudget);

        IReadOnlyDictionary<string, HopAnnotation> annotations;

        try
        {
            annotations = await _annotator
                .AnnotateAsync([.. state.Responders], budget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || budget.IsCancellationRequested)
        {
            // Обогащение — украшение поверх измерения. Его отказ не должен отменять
            // результат, который уже получен.
            return;
        }

        var publicHops = 0;

        foreach (var address in state.Responders)
        {
            if (!annotations.TryGetValue(address, out var annotation))
            {
                continue;
            }

            if (!annotation.IsPrivate)
            {
                publicHops++;
            }

            if (annotation.Describe() is { Length: > 0 } text)
            {
                observer.OnFact(ProbeFact.Text(HopAnnotation.FactCategory, address, text));
            }
        }

        if (_annotator.Attribution is { } attribution)
        {
            observer.OnFact(ProbeFact.Text("path", "Источник данных", attribution));
        }
        else if (publicHops > 0)
        {
            observer.OnFact(ProbeFact.Text(
                "path",
                "Принадлежность узлов",
                "база не найдена — номера автономных систем не показаны. "
                + $"Положите базу DB-IP Lite (.mmdb) в каталог {_annotator.AsnDatabaseHint}."));
        }
    }

    private static void ReportSummary(
        IProbeObserver observer,
        TraceState state,
        int maxHops,
        int rounds,
        bool cancelled)
    {
        if (cancelled)
        {
            observer.OnFact(ProbeFact.Text(
                "path",
                "Наблюдение",
                "остановлено оператором. Ниже — итог по тому, что успели измерить."));
        }

        if (state.Reached)
        {
            observer.OnFact(ProbeFact.Text("path", "Итог", $"цель {state.DestinationAddress} достигнута"));
        }
        else
        {
            observer.OnFact(ProbeFact.Warning(
                "path",
                "Итог",
                $"цель {state.DestinationAddress} не достигнута за {maxHops} хопов. "
                + (state.LastResponder is null
                    ? "Ни один хоп не ответил — вероятно, ICMP фильтруется целиком."
                    : $"Последний ответивший узел: {state.LastResponder}.")));
        }

        if (rounds > 1)
        {
            observer.OnFact(ProbeFact.Text(
                "path",
                "Режим",
                $"непрерывное наблюдение, циклов: {rounds.ToString(CultureInfo.InvariantCulture)}"));
        }

        var silent = state.SilentHops;

        // Пояснение нужно всегда, когда в маршруте есть молчащие хопы, а не только при
        // неудаче: строка со стопроцентными потерями посреди успешной трассировки
        // выглядит как авария, хотя означает лишь молчаливый узел.
        if (silent > 0)
        {
            observer.OnFact(ProbeFact.Text(
                "path",
                "О звёздочках",
                $"молчащих хопов: {silent}. Это не потери: узел может не отвечать на ICMP, "
                + "но исправно передавать транзитный трафик. Потери имеют значение только на конечном узле "
                + "и на хопах, где они начинаются и держатся до конца маршрута."));
        }
    }

    /// <summary>Результат одной пробы хопа до превращения в сэмпл.</summary>
    private readonly record struct HopProbe(
        int Ttl,
        DateTimeOffset TimestampUtc,
        double Value,
        SampleStatus Status,
        string? Responder,
        bool Reached)
    {
        public static HopProbe Failed(int ttl, DateTimeOffset timestampUtc, SampleStatus status) =>
            new(ttl, timestampUtc, 0, status, null, Reached: false);
    }

    /// <summary>
    /// Состояние одной трассировки: то, что нельзя держать на самой пробе.
    /// </summary>
    /// <remarks>
    /// Пробы регистрируются как singleton — по той же причине, по которой факты уходят
    /// в наблюдателя, а не копятся в поле.
    /// </remarks>
    private sealed class TraceState(string destinationAddress)
    {
        private readonly HashSet<int> _answered = [];

        public string DestinationAddress { get; } = destinationAddress;

        /// <summary>Сколько хопов опрашивается в цикле наблюдения.</summary>
        public int PathLength { get; set; }

        public bool Reached { get; private set; }

        public string? LastResponder { get; private set; }

        /// <summary>Уникальные адреса, ответившие хоть раз, — в порядке появления.</summary>
        public List<string> Responders { get; } = [];

        public int SilentHops => Math.Max(0, PathLength - _answered.Count);

        private int _sequence;

        public Sample Accept(HopProbe probe)
        {
            if (probe.Reached)
            {
                Reached = true;
            }

            if (probe.Responder is { } responder)
            {
                LastResponder = responder;
                _answered.Add(probe.Ttl);

                if (!Responders.Contains(responder, StringComparer.Ordinal))
                {
                    Responders.Add(responder);
                }
            }

            var sequence = _sequence++;

            return probe.Status == SampleStatus.Success
                ? new Sample
                {
                    Sequence = sequence,
                    TimestampUtc = probe.TimestampUtc,
                    Value = probe.Value,
                    Status = SampleStatus.Success,
                    Label = probe.Responder,
                    Group = probe.Ttl,
                    RespondedBy = probe.Responder,
                    Ttl = probe.Ttl,
                }
                : Sample.Failed(sequence, probe.TimestampUtc, probe.Status) with
                {
                    Group = probe.Ttl,
                    Ttl = probe.Ttl,
                };
        }
    }
}
