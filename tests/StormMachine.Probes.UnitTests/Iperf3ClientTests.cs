using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StormMachine.Probes;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки клиента iperf3 против заглушки, говорящей на том же протоколе.
/// </summary>
/// <remarks>
/// Заглушка проверяет то, что можно проверить без чужого бинаря: порядок состояний,
/// длину «печенья», кадрирование JSON и то, что счёт сервера берётся вместо своего.
/// <b>Совместимость с настоящим iperf3 этим не доказывается</b> — её проверяет оператор
/// на второй машине, и так и записано в приёмке И-13.
/// <para>
/// Числа состояний принадлежат чужому протоколу. Тест закрепляет именно их: ошибка
/// здесь означала бы, что мы разговариваем с сервером не на его языке, а молчаливо
/// на своём.
/// </para>
/// </remarks>
public sealed class Iperf3ClientTests
{
    private const sbyte TestStart = 1;
    private const sbyte TestRunning = 2;
    private const sbyte TestEnd = 4;
    private const sbyte ParamExchange = 9;
    private const sbyte CreateStreams = 10;
    private const sbyte ExchangeResults = 13;
    private const sbyte DisplayResults = 14;
    private const sbyte IperfDone = 16;
    private const sbyte AccessDenied = -1;

    /// <summary>Что заглушка увидела от клиента.</summary>
    private sealed class Seen
    {
        /// <summary>Поле, а не свойство: его увеличивают из нескольких потоков через Interlocked.</summary>
        public long BytesFromClient;

        public int CookieLength { get; set; }

        public string? Parameters { get; set; }

        public string? Results { get; set; }

        public int DataConnections { get; set; }

        public sbyte LastState { get; set; }
    }

    /// <summary>
    /// Минимальный сервер iperf3: ровно те состояния, которые проходит клиент.
    /// </summary>
    /// <remarks>
    /// Отдаёт серверный счёт байт заведомо меньше клиентского — так проверяется,
    /// что клиент берёт число принимающей стороны, а не своё.
    /// </remarks>
    private static async Task<Seen> ServeAsync(
        TcpListener listener,
        int streams,
        long serverBytes,
        CancellationToken cancellationToken,
        bool sendsData = false)
    {
        var seen = new Seen();

        using var control = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = control.GetStream();

        var cookie = new byte[37];
        await ReadExactly(stream, cookie, cancellationToken);
        seen.CookieLength = cookie.Length;

        await stream.WriteAsync(new[] { (byte)ParamExchange }, cancellationToken);
        seen.Parameters = await ReadFrame(stream, cancellationToken);

        await stream.WriteAsync(new[] { (byte)CreateStreams }, cancellationToken);

        var data = new List<TcpClient>();

        for (var i = 0; i < streams; i++)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            data.Add(client);

            var streamCookie = new byte[37];
            await ReadExactly(client.GetStream(), streamCookie, cancellationToken);
        }

        seen.DataConnections = data.Count;

        await stream.WriteAsync(new[] { (byte)TestStart }, cancellationToken);
        await stream.WriteAsync(new[] { (byte)TestRunning }, cancellationToken);

        // При обратном направлении шлёт сервер, при прямом — читает. Заглушка делает
        // ровно то, что сделал бы настоящий iperf3, иначе проверялась бы не та половина.
        var draining = data.Select(client => Task.Run(async () =>
        {
            var buffer = new byte[128 * 1024];
            Random.Shared.NextBytes(buffer);

            try
            {
                while (true)
                {
                    if (sendsData)
                    {
                        await client.GetStream().WriteAsync(buffer, cancellationToken);
                        continue;
                    }

                    var read = await client.GetStream().ReadAsync(buffer, cancellationToken);

                    if (read == 0)
                    {
                        return;
                    }

                    Interlocked.Add(ref seen.BytesFromClient, read);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }, cancellationToken)).ToList();

        var state = new byte[1];
        await ReadExactly(stream, state, cancellationToken);
        seen.LastState = (sbyte)state[0];

        await stream.WriteAsync(new[] { (byte)ExchangeResults }, cancellationToken);
        seen.Results = await ReadFrame(stream, cancellationToken);

        await WriteFrame(stream, ServerResults(streams, serverBytes), cancellationToken);
        await stream.WriteAsync(new[] { (byte)DisplayResults }, cancellationToken);

        var done = new byte[1];
        await ReadExactly(stream, done, cancellationToken);

        Assert.Equal(IperfDone, (sbyte)done[0]);

        foreach (var client in data)
        {
            client.Dispose();
        }

        await Task.WhenAll(draining);

        return seen;
    }

    /// <summary>
    /// Итоги сервера с теми номерами потоков, какие он себе назначил.
    /// </summary>
    /// <remarks>
    /// Номера намеренно с пропуском: живой <c>iperf3 -s</c> при трёх потоках выдал
    /// 1, 3 и 4. Заглушка повторяет это, потому что тест закрепляет именно вывод —
    /// номера принадлежат серверу, и складывать надо всё, что он прислал.
    /// </remarks>
    private static string ServerResults(int streams, long bytes)
    {
        var perStream = bytes / Math.Max(1, streams);
        var ids = Enumerable.Range(0, streams).Select(i => i == 0 ? 1 : i + 2).ToList();

        var streamsJson = string.Join(',', ids.Select(id =>
            $$"""{"id":{{id}},"bytes":{{perStream}},"retransmits":-1,"jitter":0,"errors":0,"packets":0}"""));

        return $$"""
            {"cpu_util_total":1,"cpu_util_user":0,"cpu_util_system":1,
             "sender_has_retransmits":-1,"streams":[{{streamsJson}}]}
            """;
    }

    private static async Task ReadExactly(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);

            if (got == 0)
            {
                throw new IOException("Поток закрылся раньше времени.");
            }

            read += got;
        }
    }

    private static async Task<string> ReadFrame(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactly(stream, header, cancellationToken);

        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await ReadExactly(stream, payload, cancellationToken);

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task WriteFrame(NetworkStream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];

        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [Fact]
    public async Task Client_WalksThroughTheProtocolAndTakesTheServerCount()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        const long ServerBytes = 4_000_000;
        var serving = ServeAsync(listener, streams: 2, ServerBytes, stop.Token);

        var result = await Iperf3Client.RunAsync(
            "127.0.0.1", port, seconds: 1, streams: 2, omitSeconds: 0, reverse: false,
            onProgress: null, cancellationToken: stop.Token);

        var seen = await serving;
        listener.Stop();

        // Протокол пройден целиком: печенье нужной длины, два потока данных,
        // конец теста объявлен клиентом.
        Assert.Equal(37, seen.CookieLength);
        Assert.Equal(2, seen.DataConnections);
        Assert.Equal(TestEnd, seen.LastState);

        // Параметры ушли в том виде, в каком их ждёт чужой сервер.
        using var parameters = JsonDocument.Parse(seen.Parameters!);
        Assert.True(parameters.RootElement.GetProperty("tcp").GetBoolean());
        Assert.Equal(1, parameters.RootElement.GetProperty("time").GetInt32());
        Assert.Equal(2, parameters.RootElement.GetProperty("parallel").GetInt32());

        // Обратного направления не просили — поле не должно уходить вовсе.
        Assert.False(parameters.RootElement.TryGetProperty("reverse", out _));

        // Свои итоги — один сводный ряд с номером 1, сколько бы потоков ни было.
        // Номера присваивает сервер, и выдуманный номер он отвергает целиком:
        // измерение проходит, а результата нет.
        using var results = JsonDocument.Parse(seen.Results!);
        var own = results.RootElement.GetProperty("streams");

        Assert.Equal(1, own.GetArrayLength());
        Assert.Equal(1, own[0].GetProperty("id").GetInt32());

        // Главное: взят счёт принимающей стороны, а не свой, и сложены все его ряды —
        // с любыми номерами, которые он себе назначил.
        Assert.Equal(ServerBytes, result.BytesReceived);
        Assert.True(result.BytesSent > 0);
        Assert.Contains("принимающей", result.CountedBy, StringComparison.Ordinal);
        Assert.Equal(2, result.Streams);
    }

    [Fact]
    public async Task Reverse_IsAskedForExplicitly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var serving = ServeAsync(listener, streams: 1, 1_000_000, stop.Token, sendsData: true);

        var result = await Iperf3Client.RunAsync(
            "127.0.0.1", port, seconds: 1, streams: 1, omitSeconds: 0, reverse: true,
            onProgress: null, cancellationToken: stop.Token);

        var seen = await serving;
        listener.Stop();

        using var parameters = JsonDocument.Parse(seen.Parameters!);
        Assert.True(parameters.RootElement.GetProperty("reverse").GetBoolean());

        // При обратном направлении принимаем мы, и наш счёт — это дошедшее.
        Assert.True(result.BytesReceived > 0);
        Assert.True(result.Reverse);
    }

    [Fact]
    public async Task BusyServer_IsExplainedNotSwallowed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serving = Task.Run(async () =>
        {
            using var control = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = control.GetStream();

            var cookie = new byte[37];
            await ReadExactly(stream, cookie, stop.Token);

            // Сервер iperf3 обслуживает одного клиента за раз и остальным отказывает.
            await stream.WriteAsync(new[] { unchecked((byte)AccessDenied) }, stop.Token);
            await stream.FlushAsync(stop.Token);
        }, stop.Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Iperf3Client.RunAsync("127.0.0.1", port, 1, 1, 0, false, null, stop.Token));

        await serving;
        listener.Stop();

        Assert.Contains("занят", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServer_TellsWhatToCheck()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Iperf3Client.RunAsync("127.0.0.1", free, 1, 1));

        Assert.Contains("iperf3 -s", error.Message, StringComparison.Ordinal);
    }
}
