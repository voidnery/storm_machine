using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace StormMachine.Protocol.Traffic;

/// <summary>
/// Пропускная способность по TCP: N потоков, прогрев, отбрасывание разгона.
/// </summary>
/// <remarks>
/// Один поток не наполняет канал. Пропускная способность одного соединения ограничена
/// окном, делённым на RTT, и на канале 100 Мбит/с с задержкой 30 мс одиночный поток
/// упрётся заметно ниже линии. Несколько потоков — не хитрость, а условие того, чтобы
/// измерялся канал, а не окно (RFC 6349 §5).
/// <para>
/// Первые секунды отбрасываются. TCP начинает медленно и разгоняется, и включить разгон
/// в среднее значит занизить результат тем сильнее, чем короче тест. Отброшенное время
/// показывается оператору: измерение, которое молчит о том, что выбросило, проверить
/// нельзя.
/// </para>
/// </remarks>
public static class TcpThroughput
{
    /// <summary>Размер буфера отправки и приёма. Меньше — упрёмся в число системных вызовов.</summary>
    private const int BufferBytes = 256 * 1024;

    /// <summary>Преамбула потока: идентификатор теста и номер потока.</summary>
    private const int PreambleBytes = 20;

    /// <summary>Как часто отдавать снимок для живого показа.</summary>
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Отправляет поток и возвращает то, что успел отдать.
    /// </summary>
    /// <remarks>
    /// Отправитель считает отданное в сокет, а это не то же самое, что дошло: потери
    /// видны только на приёме. Итог измерения берётся у принимающей стороны, здесь —
    /// лишь то, что было предложено каналу.
    /// </remarks>
    public static async Task<TestSnapshot> SendAsync(
        string host,
        int port,
        TestRequest request,
        IProgress<TestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(request);

        var streams = Math.Max(1, request.Streams);
        var total = new StreamCounters();
        var watch = Stopwatch.StartNew();
        var warmup = TimeSpan.FromSeconds(Math.Max(0, request.WarmupSeconds));
        var duration = warmup + TimeSpan.FromSeconds(Math.Max(1, request.DurationSeconds));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reporter = ReportAsync(total, watch, warmup, progress, stop.Token);

        var pumps = Enumerable.Range(0, streams).Select(index => Task.Run(async () =>
        {
            using var client = new TcpClient { SendBufferSize = BufferBytes, NoDelay = true };
            await client.ConnectAsync(host, port, stop.Token).ConfigureAwait(false);

            var stream = client.GetStream();
            await stream.WriteAsync(Preamble(request.Id, index), stop.Token).ConfigureAwait(false);

            // Один буфер на поток, заполненный один раз: содержимое не имеет значения,
            // а перезаполнять его каждый раз значило бы мерить скорость памяти.
            var payload = new byte[BufferBytes];
            Random.Shared.NextBytes(payload);

            while (watch.Elapsed < duration && !stop.IsCancellationRequested)
            {
                await stream.WriteAsync(payload, stop.Token).ConfigureAwait(false);
                total.Add(payload.Length, watch.Elapsed >= warmup);
            }
        }, stop.Token));

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Swallow(reporter).ConfigureAwait(false);

            return Finish(request.Id, total, watch.Elapsed - warmup, Describe(ex));
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await Swallow(reporter).ConfigureAwait(false);

        return Finish(request.Id, total, watch.Elapsed - warmup, null);
    }

    /// <summary>
    /// Принимает потоки и считает то, что дошло на самом деле.
    /// </summary>
    /// <remarks>
    /// Слушатель передаётся снаружи: порт объявляется собеседнику отдельным сообщением
    /// до начала измерения, и открыть его должна та же сторона, что его назвала.
    /// </remarks>
    public static async Task<TestSnapshot> ReceiveAsync(
        TcpListener listener,
        TestRequest request,
        IProgress<TestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(request);

        var streams = Math.Max(1, request.Streams);
        var total = new StreamCounters();
        var warmup = TimeSpan.FromSeconds(Math.Max(0, request.WarmupSeconds));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Часы запускаются с первым байтом, а не с началом ожидания: время, потраченное
        // отправителем на установление соединений, к пропускной способности не относится.
        var watch = new Stopwatch();
        var pumps = new List<Task>(streams);

        try
        {
            for (var i = 0; i < streams; i++)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);

                pumps.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        client.ReceiveBufferSize = BufferBytes;
                        var stream = client.GetStream();
                        var buffer = new byte[BufferBytes];

                        var preamble = new byte[PreambleBytes];
                        await ReadExactlyAsync(stream, preamble, stop.Token).ConfigureAwait(false);

                        if (!MatchesTest(preamble, request.Id))
                        {
                            throw new ProtocolException(
                                "Поток данных пришёл с чужим идентификатором измерения. "
                                + "Порт занят другим тестом или чужой программой.");
                        }

                        lock (total)
                        {
                            if (!watch.IsRunning)
                            {
                                watch.Start();
                            }
                        }

                        while (!stop.IsCancellationRequested)
                        {
                            var read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);

                            if (read == 0)
                            {
                                return;
                            }

                            total.Add(read, watch.Elapsed >= warmup);
                        }
                    }
                }, stop.Token));
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            await stop.CancelAsync().ConfigureAwait(false);

            return Finish(request.Id, total, watch.Elapsed - warmup, Describe(ex));
        }

        var reporter = ReportAsync(total, watch, warmup, progress, stop.Token);

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProtocolException)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Swallow(reporter).ConfigureAwait(false);

            return Finish(request.Id, total, watch.Elapsed - warmup, Describe(ex));
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await Swallow(reporter).ConfigureAwait(false);

        return Finish(request.Id, total, watch.Elapsed - warmup, null);
    }

    public static byte[] Preamble(Guid id, int streamIndex)
    {
        var preamble = new byte[PreambleBytes];
        id.TryWriteBytes(preamble);
        BinaryPrimitives.WriteInt32BigEndian(preamble.AsSpan(16), streamIndex);

        return preamble;
    }

    public static bool MatchesTest(ReadOnlySpan<byte> preamble, Guid id) =>
        preamble.Length >= PreambleBytes && new Guid(preamble[..16]) == id;

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);

            if (got == 0)
            {
                throw new ProtocolException("Поток данных закрылся, не назвав измерение.");
            }

            read += got;
        }
    }

    private static async Task ReportAsync(
        StreamCounters counters,
        Stopwatch watch,
        TimeSpan warmup,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        // Снимок несёт скорость за прошедший отрезок, а не среднюю с начала.
        // Среднее сглаживает: канал, просевший на секунду в середине теста, на графике
        // средних выглядит как ровная линия с чуть меньшим наклоном, и увидеть просадку
        // нельзя. Итоговое же число остаётся средним — там вопрос другой.
        var previousBytes = 0L;
        var previousElapsed = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SnapshotInterval, cancellationToken).ConfigureAwait(false);

                var elapsed = watch.Elapsed - warmup;

                // Отрезок короче половины периода не годится: сразу после прогрева
                // до первого такта проходят микросекунды, а байты за них уже посчитаны,
                // и деление даёт число в тысячи раз выше возможного. Один такой отсчёт
                // портит и медиану, и разброс — то есть весь ответ.
                if (elapsed - previousElapsed < SnapshotInterval / 2)
                {
                    continue;
                }

                var bytes = counters.MeasuredBytes;

                progress.Report(new TestSnapshot
                {
                    Id = Guid.Empty,
                    ElapsedSeconds = elapsed.TotalSeconds,
                    Bytes = bytes,
                    Packets = 0,
                    Mbps = Mbps(bytes - previousBytes, elapsed - previousElapsed),
                });

                previousBytes = bytes;
                previousElapsed = elapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // Измерение закончилось — отчитываться больше не о чем.
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TestSnapshot Finish(Guid id, StreamCounters counters, TimeSpan measured, string? failure) => new()
    {
        Id = id,
        ElapsedSeconds = Math.Max(0, measured.TotalSeconds),
        Bytes = counters.MeasuredBytes,
        Packets = 0,
        Mbps = Mbps(counters.MeasuredBytes, measured),
        IsFinal = true,
        Failure = failure,
    };

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000.0;

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Измерение прервано.",
        SocketException socket => $"Соединение потока данных: {socket.SocketErrorCode}.",
        ProtocolException protocol => protocol.Message,
        _ => ex.Message,
    };

    /// <summary>
    /// Счётчики, разделённые на прогрев и измерение.
    /// </summary>
    /// <remarks>
    /// Отброшенное на разгоне считается отдельно, а не выбрасывается молча: оператор
    /// должен видеть, что именно исключено из результата.
    /// </remarks>
    private sealed class StreamCounters
    {
        private long _warmupBytes;
        private long _measuredBytes;

        public long WarmupBytes => Interlocked.Read(ref _warmupBytes);

        public long MeasuredBytes => Interlocked.Read(ref _measuredBytes);

        public void Add(int bytes, bool measured)
        {
            if (measured)
            {
                Interlocked.Add(ref _measuredBytes, bytes);
            }
            else
            {
                Interlocked.Add(ref _warmupBytes, bytes);
            }
        }
    }
}
