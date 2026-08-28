using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StormMachine.Protocol;
using StormMachine.Protocol.Traffic;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Проверки механики измерений.
/// </summary>
/// <remarks>
/// Идут через настоящие сокеты на петле. Петля — не сеть, и числа здесь ничего не говорят
/// о канале; проверяется другое: что счётчики считают то, что названо, что прогрев
/// действительно отбрасывается и что потери и переупорядочивание распознаются.
/// Точность на настоящем канале — предмет И-13, для которой нужна вторая машина.
/// </remarks>
public sealed class TrafficTests
{
    [Fact]
    public void Pacer_KeepsTheInterval()
    {
        // Спайк-05 намерил ошибку такта p99 = 0.000 мс. Здесь порог намеренно
        // грубее: тест идёт вместе с остальными и на занятой машине.
        var pacer = new PacketPacer(1.0);
        var errors = new List<double>(200);
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < 200; i++)
        {
            errors.Add(pacer.WaitForNext());
        }

        watch.Stop();

        errors.Sort();

        Assert.True(errors[190] < 1.0, $"p95 ошибки такта {errors[190]:0.000} мс — темповка не держит интервал.");
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 190, 260);
    }

    [Fact]
    public void Pacer_DoesNotAccumulateDrift()
    {
        // Следующий такт отсчитывается от намеченного, а не от фактического момента.
        // Иначе за тысячу тактов набежала бы заметная разница, и скорость считалась бы
        // по неверной длительности.
        var pacer = new PacketPacer(0.5);
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < 400; i++)
        {
            pacer.WaitForNext();
        }

        watch.Stop();

        Assert.InRange(watch.Elapsed.TotalMilliseconds, 190, 230);
    }

    [Fact]
    public void Pacer_SkipsMissedTicksInsteadOfCatchingUp()
    {
        var pacer = new PacketPacer(1.0);
        Thread.Sleep(50);

        var skipped = pacer.SkipMissed();

        // Честно догонять пятьдесят тактов значило бы выдать очередь пакетов подряд.
        Assert.True(skipped > 30, $"Пропущено всего {skipped} тактов после паузы в 50 мс.");
        Assert.True(pacer.WaitForNext() < 5);
    }

    [Theory]
    [InlineData(10.0, 172, 0.1376)]
    [InlineData(100.0, 1400, 0.112)]
    [InlineData(1.0, 1400, 11.2)]
    public void Pacer_IntervalFollowsFromRate(double mbps, int payload, double expectedMs) =>
        Assert.Equal(expectedMs, PacketPacer.IntervalFor(mbps, payload), 4);

    [Fact]
    public void Preamble_IdentifiesTheTest()
    {
        var id = Guid.NewGuid();
        var preamble = TcpThroughput.Preamble(id, 3);

        Assert.True(TcpThroughput.MatchesTest(preamble, id));
        Assert.False(TcpThroughput.MatchesTest(preamble, Guid.NewGuid()));
    }

    [Fact]
    public async Task TcpThroughput_CountsWhatArrived()
    {
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.TcpThroughput,
            Streams = 2,
            WarmupSeconds = 0,
            DurationSeconds = 1,
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receiving = TcpThroughput.ReceiveAsync(listener, request);
        var sending = TcpThroughput.SendAsync("127.0.0.1", port, request);

        var sent = await sending;
        var received = await receiving;

        listener.Stop();

        Assert.Null(sent.Failure);
        Assert.True(received.Bytes > 0, "Принимающая сторона не насчитала ни одного байта.");
        Assert.True(received.Mbps > 0);
        Assert.True(received.IsFinal);
        Assert.Equal(request.Id, received.Id);
    }

    [Fact]
    public async Task TcpThroughput_WarmupIsExcluded()
    {
        // Прогрев отбрасывается: TCP разгоняется, и включить разгон в среднее
        // значит занизить результат тем сильнее, чем короче тест.
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.TcpThroughput,
            Streams = 1,
            WarmupSeconds = 1,
            DurationSeconds = 1,
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receiving = TcpThroughput.ReceiveAsync(listener, request);
        await TcpThroughput.SendAsync("127.0.0.1", port, request);
        var received = await receiving;

        listener.Stop();

        // Измеренное время — без прогрева, иначе скорость делилась бы на чужую секунду.
        Assert.InRange(received.ElapsedSeconds, 0.5, 1.9);
    }

    [Fact]
    public async Task TcpThroughput_ForeignStreamIsRefused()
    {
        // Порт данных открыт наружу. Чужой поток на нём — это либо другой тест,
        // либо посторонняя программа, и считать его своим нельзя.
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.TcpThroughput,
            Streams = 1,
            WarmupSeconds = 0,
            DurationSeconds = 1,
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiving = TcpThroughput.ReceiveAsync(listener, request);

        using (var intruder = new TcpClient())
        {
            await intruder.ConnectAsync(IPAddress.Loopback, port);
            await intruder.GetStream().WriteAsync(TcpThroughput.Preamble(Guid.NewGuid(), 0));
            await intruder.GetStream().FlushAsync();

            var result = await receiving;

            Assert.NotNull(result.Failure);
            Assert.Contains("чужим идентификатором", result.Failure, StringComparison.Ordinal);
        }

        listener.Stop();
    }

    [Fact]
    public async Task UdpQuality_CountsPacketsAndFindsNoLossOnLoopback()
    {
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            WarmupSeconds = 0,
            DurationSeconds = 1,
            TargetMbps = 2,
            PayloadBytes = 172,
        };

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        var receiving = UdpQuality.ReceiveAsync(socket, request);
        var sent = await UdpQuality.SendAsync("127.0.0.1", port, request);
        var received = await receiving;

        Assert.True(sent.Packets > 100, $"Отправлено всего {sent.Packets} пакетов.");
        Assert.True(received.Packets > 0, "Ни один пакет не дошёл по петле.");
        Assert.Equal(0, received.OutOfOrder);
        Assert.True(received.JitterMs >= 0);
        Assert.True(received.IsFinal);
    }

    [Fact]
    public async Task UdpQuality_ForeignPacketsAreIgnored()
    {
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            WarmupSeconds = 0,
            DurationSeconds = 1,
            TargetMbps = 1,
            PayloadBytes = 172,
        };

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var receiving = UdpQuality.ReceiveAsync(socket, request);

        // Чужой поток на том же порту не должен попасть в счётчики.
        using (var intruder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            var alien = new byte[200];
            Guid.NewGuid().TryWriteBytes(alien);

            for (var i = 0; i < 50; i++)
            {
                intruder.SendTo(alien, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
            }
        }

        await UdpQuality.SendAsync("127.0.0.1", port, request);
        var received = await receiving;

        // Наши пакеты посчитаны, чужие — нет. Иначе потери считались бы от чужого потока.
        Assert.True(received.Packets > 0);
        Assert.True(received.Packets < 10_000);
    }

    [Fact]
    public async Task UdpQuality_WarmupIsNotCountedAsLoss()
    {
        // Здесь была настоящая ошибка: номер последнего пакета включал прогрев,
        // а число принятых — нет, и на петле без единой потери проба показывала
        // ровно столько «потерь», сколько длился прогрев от всего измерения.
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            WarmupSeconds = 1,
            DurationSeconds = 2,
            TargetMbps = 4,
            PayloadBytes = 172,
        };

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 8 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        var receiving = UdpQuality.ReceiveAsync(socket, request);
        await UdpQuality.SendAsync("127.0.0.1", port, request);
        var received = await receiving;

        Assert.True(received.Packets > 0, "Ни один пакет не дошёл по петле.");
        Assert.Equal(0, received.Lost);
    }

    [Fact]
    public async Task UdpQuality_RateIsMeasuredBetweenPacketsNotUntilTimeout()
    {
        // Окно ожидания опоздавших не должно попадать в знаменатель: оно занижало
        // скорость тем сильнее, чем короче измерение.
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            WarmupSeconds = 0,
            DurationSeconds = 2,
            TargetMbps = 8,
            PayloadBytes = 172,
        };

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 8 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        var receiving = UdpQuality.ReceiveAsync(socket, request);
        await UdpQuality.SendAsync("127.0.0.1", port, request);
        var received = await receiving;

        // Петля отдаёт заказанное почти точно. Границы широкие: тест идёт вместе
        // с остальными и на занятой машине.
        Assert.InRange(received.Mbps, 7.0, 9.0);
    }

    [Fact]
    public async Task UdpQuality_SilenceIsExplained()
    {
        var request = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            WarmupSeconds = 0,
            DurationSeconds = 1,
            TargetMbps = 1,
        };

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var received = await UdpQuality.ReceiveAsync(socket, request, cancellationToken: stop.Token);

        Assert.Equal(0, received.Packets);
        Assert.Contains("брандмауэр", received.Failure ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
