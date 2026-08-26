using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Проба traceroute: маршрут до цели с задержкой на каждом хопе.
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
/// </remarks>
public sealed class TracerouteProbe(IHighResolutionClock clock, TargetResolver resolver) : IProbe
{
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TargetResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Traceroute,
        Shape = ProbeResultShape.PathTrace,
        Name = "trace",
        Title = "Traceroute",
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
                Name = "attempts", Label = "Попыток на хоп", Type = ProbeParameterType.Integer,
                DefaultValue = 3, Minimum = 1, Maximum = 100,
                Description = "Сколько пакетов отправить с каждым TTL.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
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
        var timeoutMs = request.GetParameter("timeout", 2000);
        var payloadBytes = request.GetParameter("size", 32);

        var destination = await _resolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        observer.OnResolved(destination.ToString());

        var buffer = new byte[payloadBytes];
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        using var ping = new Ping();

        // Прогрев асинхронного пути — та же причина, что и в ICMP-пробе: первый вызов
        // тянет за собой компиляцию и завышает измерение (см. И-1).
        try
        {
            await ping.SendPingAsync(IPAddress.Loopback, TimeSpan.FromMilliseconds(500), buffer, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        var sequence = 0;
        var reached = false;
        var lastResponder = string.Empty;
        var silentHops = 0;

        for (var ttl = 1; ttl <= maxHops && !reached; ttl++)
        {
            var options = new PingOptions(ttl, false);
            var hopAnswered = false;

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startedAt = _clock.GetTimestamp();
                var timestampUtc = DateTimeOffset.UtcNow;

                Sample sample;
                try
                {
                    var reply = await ping
                        .SendPingAsync(destination, timeout, buffer, options, cancellationToken)
                        .ConfigureAwait(false);

                    var elapsed = _clock.ElapsedMilliseconds(startedAt);
                    var responder = reply.Address?.ToString();

                    if (reply.Status == IPStatus.Success)
                    {
                        reached = true;
                    }

                    if (!string.IsNullOrEmpty(responder) && responder != "0.0.0.0")
                    {
                        lastResponder = responder;
                        hopAnswered = true;
                    }

                    sample = reply.Status switch
                    {
                        IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded => new Sample
                        {
                            Sequence = sequence,
                            TimestampUtc = timestampUtc,
                            Value = elapsed,
                            Status = SampleStatus.Success,
                            Label = responder,
                            Group = ttl,
                            RespondedBy = responder,
                            Ttl = ttl,
                        },
                        IPStatus.TimedOut => Hidden(sequence, timestampUtc, ttl, SampleStatus.Timeout),
                        _ => Hidden(sequence, timestampUtc, ttl, SampleStatus.Unreachable),
                    };
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (PingException)
                {
                    sample = Hidden(sequence, timestampUtc, ttl, SampleStatus.Error);
                }

                sequence++;
                yield return sample;
            }

            if (!hopAnswered)
            {
                silentHops++;
            }
        }

        ReportSummary(observer, destination, reached, lastResponder, maxHops, silentHops);
    }

    private static void ReportSummary(
        IProbeObserver observer,
        IPAddress destination,
        bool reached,
        string lastResponder,
        int maxHops,
        int silentHops)
    {
        if (reached)
        {
            observer.OnFact(ProbeFact.Text("path", "Итог", $"цель {destination} достигнута"));
        }
        else
        {
            observer.OnFact(ProbeFact.Warning(
                "path",
                "Итог",
                $"цель {destination} не достигнута за {maxHops} хопов. "
                + (string.IsNullOrEmpty(lastResponder)
                    ? "Ни один хоп не ответил — вероятно, ICMP фильтруется целиком."
                    : $"Последний ответивший узел: {lastResponder}.")));
        }

        // Пояснение нужно всегда, когда в маршруте есть молчащие хопы, а не только при
        // неудаче: строка со стопроцентными потерями посреди успешной трассировки
        // выглядит как авария, хотя означает лишь молчаливый узел.
        if (silentHops > 0)
        {
            observer.OnFact(ProbeFact.Text(
                "path",
                "О звёздочках",
                $"молчащих хопов: {silentHops}. Это не потери: узел может не отвечать на ICMP, "
                + "но исправно передавать транзитный трафик. Потери имеют значение только на конечном узле "
                + "и на хопах, где они начинаются и держатся до конца маршрута."));
        }
    }

    private static Sample Hidden(int sequence, DateTimeOffset timestampUtc, int ttl, SampleStatus status) =>
        Sample.Failed(sequence, timestampUtc, status) with { Group = ttl, Ttl = ttl };
}
