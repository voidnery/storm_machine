using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Проба UDP: отправка датаграммы и ожидание ответа.
/// </summary>
/// <remarks>
/// У UDP нет установления соединения, поэтому молчание неоднозначно: пакет мог пропасть,
/// порт мог быть закрыт без уведомления, ответ мог не предполагаться протоколом. Проба
/// честно разделяет два различимых исхода — ответ получен и порт явно недоступен
/// (пришёл ICMP Port Unreachable) — и не выдаёт молчание за отказ.
/// <para>
/// Осмысленна для протоколов, где ответ предусмотрен: DNS (53), NTP (123), echo (7).
/// По умолчанию отправляется корректный запрос DNS, потому что на него отвечают чаще всего.
/// </para>
/// </remarks>
public sealed class UdpProbe(IHighResolutionClock clock, TargetResolver resolver) : IProbe
{
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TargetResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Udp,
        Name = "udp",
        Title = "UDP-проба",
        Description = "Отправка датаграммы и ожидание ответа. Различает ответ, молчание и явный отказ порта.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.UdpProbe,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "port", Label = "Порт", Type = ProbeParameterType.Integer,
                DefaultValue = 53, Minimum = 1, Maximum = 65535,
                Description = "Порт назначения.",
            },
            new ProbeParameter
            {
                Name = "count", Label = "Число проб", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 1_000_000,
                Description = "Сколько датаграмм отправить.",
            },
            new ProbeParameter
            {
                Name = "interval", Label = "Интервал, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 1000, Minimum = 1, Maximum = 600_000,
                Description = "Пауза между отправками.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 2000, Minimum = 1, Maximum = 60_000,
                Description = "Сколько ждать ответа.",
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

        var port = request.GetParameter("port", 53);
        var count = request.GetParameter("count", 4);
        var intervalMs = request.GetParameter("interval", 1000);
        var timeoutMs = request.GetParameter("timeout", 2000);

        var address = await _resolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        observer.OnResolved($"{address}:{port}");

        var endpoint = new IPEndPoint(address, port);
        var payload = DnsWire.BuildQuery(0x5354, "storm-machine-probe.invalid", DnsWire.RecordTypeA);
        var receiveBuffer = new byte[4096];

        observer.OnFact(ProbeFact.Text("udp", "Полезная нагрузка", "запрос DNS типа A к несуществующему имени"));

        var silent = 0;
        var rejected = 0;

        for (var sequence = 0; sequence < count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = _clock.GetTimestamp();
            var timestampUtc = DateTimeOffset.UtcNow;

            var sample = await SendOnceAsync(
                endpoint, payload, receiveBuffer, sequence, timestampUtc, startedAt, timeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (sample.Status == SampleStatus.Timeout)
            {
                silent++;
            }
            else if (sample.Status == SampleStatus.Rejected)
            {
                rejected++;
            }

            yield return sample;

            if (sequence + 1 < count)
            {
                await ProbePacing.WaitUntilNextAsync(_clock, startedAt, intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        if (silent == count)
        {
            // Важное различие для диагностики: полное молчание UDP не означает недоступность.
            observer.OnFact(ProbeFact.Warning(
                "udp",
                "Итог",
                "Ответов нет. Для UDP это не равно недоступности: порт может быть открыт и молчать."));
        }
        else if (rejected == count)
        {
            // А это уже не догадка: узел сам сказал, что порт закрыт.
            observer.OnFact(ProbeFact.Text(
                "udp",
                "Итог",
                "Порт явно недоступен: на каждую датаграмму пришёл ICMP «порт недоступен»."));
        }
    }

    private async Task<Sample> SendOnceAsync(
        IPEndPoint endpoint,
        byte[] payload,
        byte[] receiveBuffer,
        int sequence,
        DateTimeOffset timestampUtc,
        long startedAt,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        // ICMP Port Unreachable обязан доходить до пробы: «явный отказ порта» — одно
        // из трёх различий, ради которых она существует. Раньше здесь стоял
        // SIO_UDP_CONNRESET = 0, глушивший это уведомление «для предсказуемости», —
        // и закрытый порт девять итераций выглядел молчанием (найдено стендом И-24).
        // Сокет одноразовый, поэтому штатное поведение Windows атрибутирует отказ
        // ровно той отправке, которая его вызвала.

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await socket.SendToAsync(payload, SocketFlags.None, endpoint, timeoutCts.Token).ConfigureAwait(false);

            var received = await socket
                .ReceiveFromAsync(receiveBuffer, SocketFlags.None, endpoint, timeoutCts.Token)
                .ConfigureAwait(false);

            return new Sample
            {
                Sequence = sequence,
                TimestampUtc = timestampUtc,
                Value = _clock.ElapsedMilliseconds(startedAt),
                Status = SampleStatus.Success,
                RespondedBy = received.ReceivedBytes > 0 ? null : "пустой ответ",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Sample.Failed(sequence, timestampUtc, SampleStatus.Timeout);
        }
        catch (SocketException ex)
        {
            var status = ex.SocketErrorCode switch
            {
                SocketError.ConnectionReset => SampleStatus.Rejected,
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => SampleStatus.Unreachable,
                _ => SampleStatus.Error,
            };

            return Sample.Failed(sequence, timestampUtc, status);
        }
    }
}
