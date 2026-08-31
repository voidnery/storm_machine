using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using StormMachine.Probes;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// UDP-проба различает три исхода, которые обещает описанием.
/// </summary>
/// <remarks>
/// Найдено стендом И-24: проба глушила ICMP «порт недоступен» флагом
/// SIO_UDP_CONNRESET, и «явный отказ порта» девять итераций был недостижим —
/// закрытый порт выглядел молчанием. Оба конца проверки настоящие: сокеты
/// на loopback, а не подделанные исключения.
/// </remarks>
public sealed class UdpProbeTests
{
    private sealed class TestClock : IHighResolutionClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public double ResolutionNanoseconds => 1_000_000_000.0 / Stopwatch.Frequency;

        public double CalibrationBaselineMs => 0;

        public long GetTimestamp() => _watch.ElapsedTicks;

        public double ElapsedMilliseconds(long startTimestamp) =>
            ElapsedMilliseconds(startTimestamp, _watch.ElapsedTicks);

        public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) =>
            (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public bool IsElevated => false;

        public IReadOnlyList<NetworkAdapter> GetAdapters() => [];

        public NetworkAdapter? GetPrimaryAdapter() => null;
    }

    [Fact(DisplayName = "Закрытый порт — явный отказ, а не молчание")]
    public async Task ClosedPort_IsRejected()
    {
        // Порт гарантированно закрыт: только что был наш и освобождён.
        int closedPort;
        using (var placeholder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            placeholder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)placeholder.LocalEndPoint!).Port;
        }

        var (samples, observer) = await RunAsync(closedPort);

        Assert.All(samples, s => Assert.Equal(SampleStatus.Rejected, s.Status));
        Assert.Contains(observer.Facts, f => f.Value.Contains("явно недоступен", StringComparison.Ordinal));
        Assert.DoesNotContain(observer.Facts, f => f.Value.Contains("может быть открыт и молчать", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Отвечающий порт — успех с измеренным временем")]
    public async Task AnsweringPort_IsSuccess()
    {
        using var responder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        responder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.LocalEndPoint!).Port;

        using var stop = new CancellationTokenSource();
        var echo = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!stop.Token.IsCancellationRequested)
            {
                var received = await responder.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                await responder.SendToAsync(
                    buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, received.RemoteEndPoint, stop.Token);
            }
        });

        try
        {
            var (samples, _) = await RunAsync(port);

            Assert.All(samples, s => Assert.Equal(SampleStatus.Success, s.Status));
            Assert.All(samples, s => Assert.True(s.Value >= 0));
        }
        finally
        {
            await stop.CancelAsync();
            responder.Close();

            try
            {
                await echo;
            }
            catch (OperationCanceledException)
            {
                // Штатная остановка отвечающего.
            }
            catch (SocketException)
            {
                // Сокет закрыт из-под приёма — это и был способ остановиться.
            }
        }
    }

    private static async Task<(List<Sample> Samples, ProbeCollector Observer)> RunAsync(int port)
    {
        var probe = new UdpProbe(new TestClock(), new TargetResolver(new FakeEnvironment()));
        var observer = new ProbeCollector();

        var request = new ProbeRequest
        {
            Target = Target.Parse("127.0.0.1"),
            Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["port"] = port,
                ["count"] = 3,
                ["interval"] = 10,
                ["timeout"] = 1000,
            },
        };

        var samples = new List<Sample>();

        await foreach (var sample in probe.ExecuteAsync(request, observer, CancellationToken.None))
        {
            samples.Add(sample);
        }

        return (samples, observer);
    }
}
