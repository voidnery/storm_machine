using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormMachine.Probes;

/// <summary>Сервер M-Lab, выбранный для измерения.</summary>
public sealed record Ndt7Server
{
    public required string Machine { get; init; }

    public string? City { get; init; }

    public string? Country { get; init; }

    public required Uri Download { get; init; }

    public required Uri Upload { get; init; }

    /// <summary>Как назвать бэкенд оператору. Показывается всегда.</summary>
    public string Describe() =>
        Machine + (City is { Length: > 0 } || Country is { Length: > 0 }
            ? $" ({string.Join(", ", new[] { City, Country }.Where(p => p is { Length: > 0 }))})"
            : string.Empty);
}

/// <summary>
/// Отсчёт скорости.
/// </summary>
/// <remarks>
/// Промежуточный несёт скорость за отрезок с прошлого отсчёта, итоговый — среднюю
/// за всю фазу. Признак нужен именно потому, что это разные величины: без него
/// последний обрывок в четверть секунды становился заголовком результата, и продукт
/// сообщал 26 Мбит/с там, где за фазу прошло 78.
/// </remarks>
public sealed record Ndt7Sample(double ElapsedSeconds, long Bytes, double Mbps, bool IsFinal = false);

/// <summary>
/// Клиент NDT7 (M-Lab).
/// </summary>
/// <remarks>
/// Публичный бэкенд выбран в исследовании (R-08): открытый протокол, открытая сеть
/// серверов, исследовательская направленность. У остальных вариантов либо лицензия,
/// либо чужая инфраструктура без формального разрешения на встраивание.
/// <para>
/// Из этого следует обязательное требование, а не пожелание: <b>всегда показывать,
/// какой сервер использован</b>. Скорость до сервера в Москве и до сервера во Франкфурте
/// — разные числа, и сравнивать их между запусками, не зная сервера, нельзя.
/// </para>
/// <para>
/// Измеряется то, что дошло до прикладного уровня. Это меньше, чем скорость канала,
/// на величину заголовков и повторных передач, и меньше, чем цифра в договоре, —
/// продукт говорит это прямо, а не выдаёт одно за другое.
/// </para>
/// </remarks>
public static class Ndt7Client
{
    /// <summary>Служба выбора ближайшего сервера.</summary>
    private const string LocateUrl = "https://locate.measurementlab.net/v2/nearest/ndt/ndt7";

    /// <summary>Подпротокол, которым сервер опознаёт клиента NDT7.</summary>
    private const string Subprotocol = "net.measurementlab.ndt.v7";

    /// <summary>Начальный размер сообщения при отдаче (спецификация ndt7).</summary>
    private const int InitialMessageBytes = 1 << 13;

    /// <summary>Предел размера сообщения при отдаче (спецификация ndt7).</summary>
    private const int MaxMessageBytes = 1 << 24;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly Ndt7JsonContext Context = new(Options);

    /// <summary>
    /// Спрашивает у M-Lab ближайший сервер.
    /// </summary>
    /// <remarks>
    /// Выбирает сам M-Lab: у него есть данные о загрузке серверов, которых у нас нет.
    /// Брать первый попавшийся из списка — значит иногда мерить до перегруженного узла
    /// и получить число, говорящее о нём, а не о канале.
    /// </remarks>
    public static async Task<Ndt7Server> LocateAsync(HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        LocateResponse? located;

        try
        {
            await using var stream = await http.GetStreamAsync(LocateUrl, cancellationToken).ConfigureAwait(false);

            located = await JsonSerializer
                .DeserializeAsync(stream, Context.LocateResponse, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                "Не удалось спросить у M-Lab ближайший сервер: " + ex.Message
                + ". Проверь доступ к locate.measurementlab.net по HTTPS.",
                ex);
        }

        var result = located?.Results?.FirstOrDefault(r => r.Urls is not null)
                     ?? throw new InvalidOperationException("M-Lab не предложил ни одного сервера.");

        var download = Url(result.Urls!, "wss:///ndt/v7/download");
        var upload = Url(result.Urls!, "wss:///ndt/v7/upload");

        return new Ndt7Server
        {
            Machine = result.Machine ?? "неизвестный сервер",
            City = result.Location?.City,
            Country = result.Location?.Country,
            Download = download,
            Upload = upload,
        };
    }

    /// <summary>Приём: сервер шлёт, мы считаем дошедшее.</summary>
    public static async IAsyncEnumerable<Ndt7Sample> DownloadAsync(
        Ndt7Server server,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var socket = Create();
        await ConnectAsync(socket, server.Download, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[1 << 20];
        var watch = Stopwatch.StartNew();

        long bytes = 0;
        var previousBytes = 0L;
        var previousElapsed = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult received;

            try
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Время фазы вышло — это штатный конец, а не потеря результата.
                // Прервать здесь исключением значило бы выбросить итоговый отсчёт
                // и оставить заголовком последний обрывок.
                break;
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            // Текстовые сообщения — измерения самого сервера. В счёт скорости приёма
            // они не идут: считается то, что пришло данными, а не разговор о них.
            if (received.MessageType == WebSocketMessageType.Binary)
            {
                bytes += received.Count;
            }

            var elapsed = watch.Elapsed;

            if (elapsed - previousElapsed < SampleInterval)
            {
                continue;
            }

            yield return new Ndt7Sample(
                elapsed.TotalSeconds,
                bytes,
                Mbps(bytes - previousBytes, elapsed - previousElapsed));

            previousBytes = bytes;
            previousElapsed = elapsed;
        }

        yield return new Ndt7Sample(watch.Elapsed.TotalSeconds, bytes, Mbps(bytes, watch.Elapsed), IsFinal: true);
    }

    /// <summary>
    /// Отдача: шлём мы.
    /// </summary>
    /// <remarks>
    /// Размер сообщения растёт по правилу спецификации: удваивается, пока не станет
    /// больше одной шестнадцатой уже отданного. Маленькие сообщения всё время упирались
    /// бы в накладные расходы кадра, а сразу большие — не дали бы отсчётов в начале.
    /// </remarks>
    public static async IAsyncEnumerable<Ndt7Sample> UploadAsync(
        Ndt7Server server,
        TimeSpan duration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var socket = Create();
        await ConnectAsync(socket, server.Upload, cancellationToken).ConfigureAwait(false);

        var size = InitialMessageBytes;
        var payload = new byte[MaxMessageBytes];
        Random.Shared.NextBytes(payload);

        var watch = Stopwatch.StartNew();

        long bytes = 0;
        var previousBytes = 0L;
        var previousElapsed = TimeSpan.Zero;

        while (watch.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await socket
                    .SendAsync(payload.AsMemory(0, size), WebSocketMessageType.Binary, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                break;
            }

            bytes += size;

            if (size < MaxMessageBytes && size < bytes / 16)
            {
                size *= 2;
            }

            var elapsed = watch.Elapsed;

            if (elapsed - previousElapsed < SampleInterval)
            {
                continue;
            }

            yield return new Ndt7Sample(
                elapsed.TotalSeconds,
                bytes,
                Mbps(bytes - previousBytes, elapsed - previousElapsed));

            previousBytes = bytes;
            previousElapsed = elapsed;
        }

        yield return new Ndt7Sample(watch.Elapsed.TotalSeconds, bytes, Mbps(bytes, watch.Elapsed), IsFinal: true);
    }

    private static ClientWebSocket Create()
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(Subprotocol);

        return socket;
    }

    private static async Task ConnectAsync(ClientWebSocket socket, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new InvalidOperationException(
                $"Не удалось соединиться с {uri.Host}: {ex.Message}. "
                + "NDT7 работает поверх WebSocket через TLS — проверь, что исходящий 443 не закрыт.",
                ex);
        }
    }

    private static Uri Url(Dictionary<string, string> urls, string key) =>
        urls.TryGetValue(key, out var value) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException(
                $"M-Lab не дал адреса «{key}». Возможно, служба выбора сервера изменила формат ответа.");

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000.0;

    internal sealed record LocateResponse
    {
        public List<LocateResult>? Results { get; init; }
    }

    internal sealed record LocateResult
    {
        public string? Machine { get; init; }

        public LocateLocation? Location { get; init; }

        public Dictionary<string, string>? Urls { get; init; }
    }

    internal sealed record LocateLocation
    {
        public string? City { get; init; }

        public string? Country { get; init; }
    }
}

[JsonSerializable(typeof(Ndt7Client.LocateResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class Ndt7JsonContext : JsonSerializerContext;
