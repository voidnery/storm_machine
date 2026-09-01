using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Проба TCP-connect: время установления соединения с портом.
/// </summary>
/// <remarks>
/// Нужна там, где ICMP закрыт политикой, — а это большинство серверов в интернете
/// и немалая часть корпоративного оборудования. Мерит не то же, что ping: сюда входит
/// работа стека на той стороне и очередь на приём соединений, поэтому значения
/// систематически выше ICMP-RTT к тому же узлу. Это не погрешность, а другая величина.
/// </remarks>
public sealed class TcpConnectProbe(IHighResolutionClock clock, TargetResolver resolver) : IProbe
{
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TargetResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.TcpConnect,
        Name = "tcp",
        Title = "TCP-connect",
        Description = "Время установления TCP-соединения с портом. Работает там, где ICMP закрыт.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.TcpConnect,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "port", Label = "Порт", Type = ProbeParameterType.Integer,
                DefaultValue = 443, Minimum = 1, Maximum = 65535,
                Description = "Порт назначения.",
            },
            new ProbeParameter
            {
                Name = "count", Label = "Число проб", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 1_000_000,
                Description = "Сколько соединений установить.",
            },
            new ProbeParameter
            {
                Name = "interval", Label = "Интервал, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 1000, Minimum = 1, Maximum = 600_000,
                Description = "Пауза между попытками.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Ждать ответа, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 3000, Minimum = 1, Maximum = 60_000,
                Description = "Сколько ждать установления соединения.",
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

        var port = request.GetParameter("port", 443);
        var count = request.GetParameter("count", 4);
        var intervalMs = request.GetParameter("interval", 1000);
        var timeoutMs = request.GetParameter("timeout", 3000);

        var address = await _resolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        observer.OnResolved($"{address}:{port}");

        var endpoint = new IPEndPoint(address, port);

        await WarmUpAsync(address.AddressFamily, cancellationToken).ConfigureAwait(false);

        for (var sequence = 0; sequence < count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = _clock.GetTimestamp();
            var timestampUtc = DateTimeOffset.UtcNow;

            var sample = await ConnectOnceAsync(endpoint, sequence, timestampUtc, startedAt, timeoutMs, cancellationToken)
                .ConfigureAwait(false);

            yield return sample;

            if (sequence + 1 < count)
            {
                await ProbePacing.WaitUntilNextAsync(_clock, startedAt, intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Прогревает асинхронный путь установления соединения.
    /// </summary>
    /// <remarks>
    /// Та же причина, что и в ICMP-пробе (И-1): первый вызов тянет за собой компиляцию
    /// и завышает измерение. На стенде без прогрева первая проба дала 168 мс против
    /// 37 мс у последующих — четырёхкратное завышение, которое при коротком прогоне
    /// испортило бы всю статистику.
    /// <para>
    /// Прогрев идёт на loopback в заведомо закрытый порт: отказ приходит мгновенно,
    /// нужный код компилируется, а в измеряемую сеть не уходит ни одного пакета.
    /// </para>
    /// </remarks>
    private static async Task WarmUpAsync(AddressFamily family, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(200);

            var closedPort = new IPEndPoint(
                family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback,
                1);

            await socket.ConnectAsync(closedPort, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Отказ здесь ожидаем и является нормой: нам нужен факт прохода по коду,
            // а не успешное соединение.
        }
    }

    private async Task<Sample> ConnectOnceAsync(
        IPEndPoint endpoint,
        int sequence,
        DateTimeOffset timestampUtc,
        long startedAt,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        // Соединение закрывается сразу и без ожидания: измеряем установление,
        // а не жизненный цикл. Иначе на серии проб останутся сокеты в TIME_WAIT
        // и порты закончатся раньше, чем закончится тест.
        socket.LingerState = new LingerOption(true, 0);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);

            return new Sample
            {
                Sequence = sequence,
                TimestampUtc = timestampUtc,
                Value = _clock.ElapsedMilliseconds(startedAt),
                Status = SampleStatus.Success,
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
            // Отказ в соединении — это ответ, а не молчание: узел жив, порт закрыт.
            // Смешивать его с таймаутом нельзя, диагностика получится разной.
            var status = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => SampleStatus.Rejected,
                SocketError.TimedOut => SampleStatus.Timeout,
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => SampleStatus.Unreachable,
                _ => SampleStatus.Error,
            };

            return Sample.Failed(sequence, timestampUtc, status);
        }
    }
}
