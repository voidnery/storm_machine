using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormMachine.Probes;

/// <summary>Итог измерения через iperf3.</summary>
public sealed record Iperf3Result
{
    public required long BytesSent { get; init; }

    /// <summary>Байты по счёту принимающей стороны. Ноль — сервер их не прислал.</summary>
    public long BytesReceived { get; init; }

    public required double Seconds { get; init; }

    public required int Streams { get; init; }

    public required bool Reverse { get; init; }

    /// <summary>Версия сервера, если он её назвал.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>
    /// Скорость по счёту принимающей стороны, если он есть, иначе по своему.
    /// </summary>
    /// <remarks>
    /// Тот же принцип, что и у собственного агента: отправитель знает, сколько отдал
    /// в сокет, а дошедшее видно только на приёме. Разница между двумя числами — это
    /// и есть потери, и подменять одно другим нельзя.
    /// </remarks>
    public double Mbps => Seconds <= 0
        ? 0
        : (BytesReceived > 0 ? BytesReceived : BytesSent) * 8 / Seconds / 1_000_000.0;

    /// <summary>Чьим счётом получено число.</summary>
    public string CountedBy => BytesReceived > 0 ? "по счёту принимающей стороны" : "по счёту отправителя";
}

/// <summary>
/// Клиент к существующему <c>iperf3 -s</c>.
/// </summary>
/// <remarks>
/// Мост в чужие сети. Решение из исследования (R-09): iperf3 под трёхпунктной BSD,
/// распространять можно — но не нужно. Официальные сборки под Windows тянут
/// <c>cygwin1.dll</c>, а свой агент делает то же самое и лучше. Совместимость нужна там,
/// где агента поставить нельзя, а iperf3 уже стоит: у провайдера, на чужом стенде,
/// на сетевом оборудовании.
/// <para>
/// Второе назначение — проверка себя. Два инструмента, меряющие один канал разными
/// реализациями, должны сходиться; расхождение означает ошибку в одном из них, и лучше
/// узнать об этом на стенде, чем в споре с провайдером.
/// </para>
/// </remarks>
public static class Iperf3Client
{
    /// <summary>Порт iperf3 по умолчанию.</summary>
    public const int DefaultPort = 5201;

    /// <summary>Длина «печенья» — опознавательной строки соединения (36 знаков и ноль).</summary>
    private const int CookieLength = 37;

    /// <summary>Размер блока данных по умолчанию — тот же, что у самого iperf3 для TCP.</summary>
    private const int BlockBytes = 128 * 1024;

    // Состояния управляющего канала iperf3. Числа принадлежат чужому протоколу
    // и менять их нельзя — они и есть договор с сервером.
    private const sbyte TestStart = 1;
    private const sbyte TestRunning = 2;
    private const sbyte TestEnd = 4;
    private const sbyte ParamExchange = 9;
    private const sbyte CreateStreams = 10;
    private const sbyte ExchangeResults = 13;
    private const sbyte DisplayResults = 14;
    private const sbyte IperfDone = 16;
    private const sbyte AccessDenied = -1;
    private const sbyte ServerError = -2;

    /// <summary>
    /// Настройки сериализации.
    /// </summary>
    /// <remarks>
    /// Условие пропуска пустых полей задаётся и здесь, и в атрибуте контекста. Это
    /// не дублирование: явно переданный экземпляр настроек перекрывает то, что объявлено
    /// атрибутом, и без этой строки в чужой сервер уходило бы <c>"reverse": null</c>
    /// там, где обратного направления не просили.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Iperf3JsonContext Context = new(Options);

    /// <summary>Как продукт представляется серверу iperf3.</summary>
    private static string ClientVersion => "storm-machine/" + ProductVersion;

    private static string ProductVersion =>
        typeof(Iperf3Client).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Проводит измерение по протоколу iperf3.
    /// </summary>
    /// <param name="onProgress">Отсчёты скорости за отрезок — для живого показа.</param>
    public static async Task<Iperf3Result> RunAsync(
        string host,
        int port,
        int seconds,
        int streams,
        int omitSeconds = 0,
        bool reverse = false,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var cookie = MakeCookie();
        using var control = new TcpClient();

        try
        {
            await control.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"До iperf3 на {host}:{port} не достучаться: {ex.SocketErrorCode}. "
                + "Проверь, что там запущен «iperf3 -s» и что входящие на этот порт разрешены.",
                ex);
        }

        var stream = control.GetStream();
        await stream.WriteAsync(cookie, cancellationToken).ConfigureAwait(false);

        var data = new List<TcpClient>(streams);
        long sent = 0;
        long received = 0;
        var elapsed = TimeSpan.Zero;
        string? serverVersion = null;

        try
        {
            while (true)
            {
                var state = await ReadStateAsync(stream, cancellationToken).ConfigureAwait(false);

                switch (state)
                {
                    case ParamExchange:
                        await SendJsonAsync(stream, Parameters(seconds, streams, omitSeconds, reverse), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case CreateStreams:
                        data.AddRange(await OpenStreamsAsync(host, port, streams, cookie, cancellationToken)
                            .ConfigureAwait(false));
                        break;

                    case TestStart:
                        break;

                    case TestRunning:
                        (sent, received, elapsed) = await PumpAsync(
                            data, seconds, reverse, onProgress, cancellationToken).ConfigureAwait(false);

                        await WriteStateAsync(stream, TestEnd, cancellationToken).ConfigureAwait(false);
                        break;

                    case ExchangeResults:
                        await SendJsonAsync(stream, OwnResults(sent, received, reverse), cancellationToken)
                            .ConfigureAwait(false);

                        var theirs = await ReadJsonAsync(stream, cancellationToken).ConfigureAwait(false);
                        var counted = CountFromServer(theirs);

                        // Счёт принимающей стороны берётся, только если он есть:
                        // старые серверы его не присылают, и подставить ноль вместо
                        // отсутствующего числа значило бы объявить, что не дошло ничего.
                        if (counted > 0)
                        {
                            if (reverse)
                            {
                                sent = counted;
                            }
                            else
                            {
                                received = counted;
                            }
                        }

                        break;

                    case DisplayResults:
                        await WriteStateAsync(stream, IperfDone, cancellationToken).ConfigureAwait(false);

                        return new Iperf3Result
                        {
                            BytesSent = sent,
                            BytesReceived = received,
                            Seconds = elapsed.TotalSeconds,
                            Streams = data.Count,
                            Reverse = reverse,
                            ServerVersion = serverVersion,
                        };

                    case AccessDenied:
                        throw new InvalidOperationException(
                            "Сервер iperf3 занят другим измерением. Он обслуживает одного клиента за раз.");

                    case ServerError:
                        throw new InvalidOperationException("Сервер iperf3 сообщил об ошибке.");

                    default:
                        throw new InvalidOperationException(
                            $"Сервер iperf3 прислал незнакомое состояние {state}. "
                            + "Возможно, версия протокола отличается.");
                }
            }
        }
        finally
        {
            foreach (var client in data)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// «Печенье» — строка, по которой сервер связывает потоки данных с управляющим каналом.
    /// </summary>
    /// <remarks>
    /// Случайная и ровно 37 байт: 36 знаков и завершающий ноль. Сервер сравнивает её
    /// побайтно, и лишний или недостающий байт означает молчаливый отказ.
    /// </remarks>
    private static byte[] MakeCookie()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        var cookie = new byte[CookieLength];

        for (var i = 0; i < CookieLength - 1; i++)
        {
            cookie[i] = (byte)alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        cookie[^1] = 0;

        return cookie;
    }

    private static async Task<List<TcpClient>> OpenStreamsAsync(
        string host,
        int port,
        int streams,
        byte[] cookie,
        CancellationToken cancellationToken)
    {
        var opened = new List<TcpClient>(streams);

        for (var i = 0; i < Math.Max(1, streams); i++)
        {
            var client = new TcpClient { NoDelay = true, SendBufferSize = BlockBytes, ReceiveBufferSize = BlockBytes };
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            await client.GetStream().WriteAsync(cookie, cancellationToken).ConfigureAwait(false);

            opened.Add(client);
        }

        return opened;
    }

    /// <summary>Гонит или принимает данные по потокам отведённое время.</summary>
    private static async Task<(long Sent, long Received, TimeSpan Elapsed)> PumpAsync(
        List<TcpClient> data,
        int seconds,
        bool reverse,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(1, seconds));
        var watch = Stopwatch.StartNew();

        long sent = 0;
        long received = 0;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Срок ставится на сам обмен, а не проверяется между чтениями. При обратном
        // направлении читаем мы, и молчащий сервер иначе подвесил бы фазу навсегда:
        // условие цикла до следующего чтения просто не доходит.
        stop.CancelAfter(duration);

        var reporter = ReportAsync(() => reverse ? Interlocked.Read(ref received) : Interlocked.Read(ref sent),
            watch, onProgress, stop.Token);

        var pumps = data.Select(client => Task.Run(async () =>
        {
            var stream = client.GetStream();
            var block = new byte[BlockBytes];

            if (!reverse)
            {
                Random.Shared.NextBytes(block);
            }

            while (watch.Elapsed < duration && !stop.IsCancellationRequested)
            {
                if (reverse)
                {
                    var read = await stream.ReadAsync(block, stop.Token).ConfigureAwait(false);

                    if (read == 0)
                    {
                        return;
                    }

                    Interlocked.Add(ref received, read);
                }
                else
                {
                    await stream.WriteAsync(block, stop.Token).ConfigureAwait(false);
                    Interlocked.Add(ref sent, block.Length);
                }
            }
        }, stop.Token));

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Поток оборвался — измеренное до обрыва остаётся в силе.
        }

        watch.Stop();
        await stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await reporter.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return (sent, received, watch.Elapsed);
    }

    private static async Task ReportAsync(
        Func<long> bytes,
        Stopwatch watch,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        if (onProgress is null)
        {
            return;
        }

        var previousBytes = 0L;
        var previousElapsed = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

            var elapsed = watch.Elapsed;
            var current = bytes();
            var seconds = (elapsed - previousElapsed).TotalSeconds;

            if (seconds <= 0)
            {
                continue;
            }

            onProgress((current - previousBytes) * 8 / seconds / 1_000_000.0);

            previousBytes = current;
            previousElapsed = elapsed;
        }
    }

    /// <summary>
    /// Параметры для сервера.
    /// </summary>
    /// <remarks>
    /// <c>omit</c> передаётся серверу, а не отбрасывается у себя: разгон обязаны
    /// отбросить обе стороны, иначе счёт принимающей стороны будет включать то,
    /// что мы у себя уже вычли, и числа разойдутся без всякой причины в канале.
    /// </remarks>
    private static Iperf3Parameters Parameters(int seconds, int streams, int omitSeconds, bool reverse) => new()
    {
        Tcp = true,
        Omit = Math.Max(0, omitSeconds),
        Time = Math.Max(1, seconds),
        Parallel = Math.Max(1, streams),
        Len = BlockBytes,
        Reverse = reverse ? true : null,
        ClientVersion = ClientVersion,
    };

    /// <summary>
    /// Свои итоги в виде одного сводного ряда.
    /// </summary>
    /// <remarks>
    /// Номера потоков присваивает <b>сервер</b>, и угадать их нельзя. Проверено на живом
    /// <c>iperf3 -s</c>: при трёх потоках он выдал номера 1, 3 и 4, а не 1, 2, 3.
    /// Узнать их заранее тоже нельзя — свои итоги клиент отправляет первым, а серверные
    /// приходят уже после. Ряд с несуществующим номером сервер отвергает целиком
    /// (<c>IESTREAMID</c>) и молча закрывает соединение: измерение прошло, а результата нет.
    /// <para>
    /// Номер 1 существует всегда. Разбивку по своим потокам сервер от нас и не ждёт:
    /// она нужна только его собственному показу, которым мы не пользуемся. Число,
    /// ради которого всё затевалось, приходит из его итогов — по счёту принимающей стороны.
    /// </para>
    /// </remarks>
    private static Iperf3Results OwnResults(long sent, long received, bool reverse) => new()
    {
        CpuUtilTotal = 0,
        CpuUtilUser = 0,
        CpuUtilSystem = 0,
        SenderHasRetransmits = -1,
        Streams =
        [
            new Iperf3StreamResult
            {
                Id = 1,
                Bytes = reverse ? received : sent,
                Retransmits = -1,
                Jitter = 0,
                Errors = 0,
                Packets = 0,
            },
        ],
    };

    /// <summary>
    /// Сумма по всем рядам сервера.
    /// </summary>
    /// <remarks>
    /// Складываются все ряды, какие бы номера у них ни были: номера — дело сервера,
    /// а нас интересует, сколько дошло всего.
    /// </remarks>
    private static long CountFromServer(Iperf3Results? results) =>
        results?.Streams is null ? 0 : results.Streams.Sum(s => s.Bytes);

    private static async Task<sbyte> ReadStateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read == 0)
        {
            throw new InvalidOperationException(
                "Сервер iperf3 закрыл управляющий канал. Возможно, он занят другим измерением.");
        }

        return (sbyte)buffer[0];
    }

    private static async Task WriteStateAsync(NetworkStream stream, sbyte state, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { (byte)state }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendJsonAsync<T>(NetworkStream stream, T value, CancellationToken cancellationToken)
        where T : class
    {
        var payload = value switch
        {
            Iperf3Parameters parameters => JsonSerializer.SerializeToUtf8Bytes(parameters, Context.Iperf3Parameters),
            Iperf3Results results => JsonSerializer.SerializeToUtf8Bytes(results, Context.Iperf3Results),
            _ => throw new InvalidOperationException($"Нечем сериализовать {typeof(T).Name}."),
        };

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Iperf3Results?> ReadJsonAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length is <= 0 or > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException($"Сервер iperf3 заявил длину ответа {length} байт.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize(payload, Context.Iperf3Results);
        }
        catch (JsonException)
        {
            // Формат итогов у разных версий отличается. Свой счёт при этом остаётся,
            // и терять всё измерение из-за неразобранного чужого ответа незачем.
            return null;
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);

            if (got == 0)
            {
                throw new InvalidOperationException("Сервер iperf3 оборвал ответ на середине.");
            }

            read += got;
        }
    }
}

/// <summary>Параметры измерения в том виде, в каком их ждёт iperf3.</summary>
internal sealed record Iperf3Parameters
{
    [JsonPropertyName("tcp")]
    public bool Tcp { get; init; }

    [JsonPropertyName("omit")]
    public int Omit { get; init; }

    [JsonPropertyName("time")]
    public int Time { get; init; }

    [JsonPropertyName("parallel")]
    public int Parallel { get; init; }

    [JsonPropertyName("len")]
    public int Len { get; init; }

    [JsonPropertyName("reverse")]
    public bool? Reverse { get; init; }

    [JsonPropertyName("client_version")]
    public string? ClientVersion { get; init; }
}

internal sealed record Iperf3StreamResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    [JsonPropertyName("retransmits")]
    public long Retransmits { get; init; }

    [JsonPropertyName("jitter")]
    public double Jitter { get; init; }

    [JsonPropertyName("errors")]
    public long Errors { get; init; }

    [JsonPropertyName("packets")]
    public long Packets { get; init; }
}

internal sealed record Iperf3Results
{
    [JsonPropertyName("cpu_util_total")]
    public double CpuUtilTotal { get; init; }

    [JsonPropertyName("cpu_util_user")]
    public double CpuUtilUser { get; init; }

    [JsonPropertyName("cpu_util_system")]
    public double CpuUtilSystem { get; init; }

    [JsonPropertyName("sender_has_retransmits")]
    public int SenderHasRetransmits { get; init; }

    [JsonPropertyName("streams")]
    public List<Iperf3StreamResult>? Streams { get; init; }
}

[JsonSerializable(typeof(Iperf3Parameters))]
[JsonSerializable(typeof(Iperf3Results))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class Iperf3JsonContext : JsonSerializerContext;
