using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes;

/// <summary>
/// Проба HTTP: разбивка времени запроса по фазам.
/// </summary>
/// <remarks>
/// Ломает исходную модель сэмпла сильнее всех остальных проб: один запрос даёт
/// <b>пять длительностей сразу</b> — разрешение имени, TCP, TLS, ожидание первого байта
/// и скачивание тела. Это не пять независимых измерений во времени, а разложение одного
/// события на составляющие. Ради этого в <see cref="Sample"/> появилось поле
/// <see cref="Sample.Label"/>.
/// <para>
/// Разделение фаз — не украшение отчёта. «Сайт медленный» из-за DNS, из-за TLS и из-за
/// медленной отдачи тела требуют трёх совершенно разных действий, и без водопада
/// отличить их нельзя.
/// </para>
/// <para>
/// Клиент HTTP реализован вручную, а не через <c>HttpClient</c>: тот переиспользует
/// соединения, скрывает установление и отдаёт одно суммарное время. Для измерительного
/// инструмента это неприемлемо — мерить нужно то, что происходит, а не то, что осталось
/// от пула соединений.
/// </para>
/// </remarks>
public sealed class HttpProbe(IHighResolutionClock clock) : IProbe
{
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Http,
        Shape = ProbeResultShape.PhasedTiming,
        Name = "http",
        Title = "HTTP-инспектор",
        Description = "Водопад таймингов: DNS, TCP, TLS, первый байт, скачивание. Код ответа и заголовки.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.HttpTiming,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "count", Label = "Число запросов", Type = ProbeParameterType.Integer,
                DefaultValue = 1, Minimum = 1, Maximum = 10_000,
                Description = "Сколько раз выполнить запрос.",
            },
            new ProbeParameter
            {
                Name = "interval", Label = "Интервал, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 1000, Minimum = 1, Maximum = 600_000,
                Description = "Пауза между запросами.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 15_000, Minimum = 1, Maximum = 120_000,
                Description = "Общий предел на запрос.",
            },
            new ProbeParameter
            {
                Name = "method", Label = "Метод", Type = ProbeParameterType.Text,
                DefaultValue = "GET",
                Description = "GET или HEAD.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>(ProbeValidation.Validate(Descriptor, request));

        var method = request.GetParameter("method", "GET").ToUpperInvariant();
        if (method is not ("GET" or "HEAD"))
        {
            errors.Add(new ProbeValidationError("method", "Поддерживаются только GET и HEAD."));
        }

        return errors;
    }

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var count = request.GetParameter("count", 1);
        var intervalMs = request.GetParameter("interval", 1000);
        var timeoutMs = request.GetParameter("timeout", 15_000);
        var method = request.GetParameter("method", "GET").ToUpperInvariant();

        var uri = BuildUri(request.Target);
        observer.OnResolved(uri.ToString());

        for (var attempt = 0; attempt < count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = _clock.GetTimestamp();
            var timestampUtc = DateTimeOffset.UtcNow;

            var samples = await ExecuteOnceAsync(
                uri, method, attempt, timestampUtc, timeoutMs, attempt == 0 ? observer : NullProbeObserver.Instance, cancellationToken)
                .ConfigureAwait(false);

            foreach (var sample in samples)
            {
                yield return sample;
            }

            if (attempt + 1 < count)
            {
                await ProbePacing.WaitUntilNextAsync(_clock, startedAt, intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<Sample>> ExecuteOnceAsync(
        Uri uri,
        string method,
        int attempt,
        DateTimeOffset timestampUtc,
        int timeoutMs,
        IProbeObserver observer,
        CancellationToken cancellationToken)
    {
        var samples = new List<Sample>(5);
        var useTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        TimedConnectionResult? connection = null;

        try
        {
            connection = await TimedConnection
                .OpenAsync(_clock, uri.Host, uri.Port, useTls, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            samples.Add(Failed(attempt, timestampUtc, "connect", SampleStatus.Timeout));
            return samples;
        }
        catch (Exception ex)
        {
            observer.OnFact(ProbeFact.Warning("http", "Соединение", ex.Message));
            samples.Add(Failed(attempt, timestampUtc, "connect", SampleStatus.Unreachable));
            return samples;
        }

        await using (connection.ConfigureAwait(false))
        {
            samples.Add(Phase(attempt, timestampUtc, "dns", connection.Phases.DnsMs));
            samples.Add(Phase(attempt, timestampUtc, "connect", connection.Phases.ConnectMs));

            if (useTls)
            {
                samples.Add(Phase(attempt, timestampUtc, "tls", connection.Phases.TlsMs));

                if (connection.PolicyErrors != System.Net.Security.SslPolicyErrors.None)
                {
                    observer.OnFact(ProbeFact.Warning("http", "TLS", $"проблема сертификата: {connection.PolicyErrors}"));
                }
            }

            if (connection.Phases.Address is { } address)
            {
                observer.OnFact(ProbeFact.Text("http", "Адрес", address.ToString()));
            }

            try
            {
                var exchange = await PerformExchangeAsync(
                    connection.Stream, uri, method, timeoutCts.Token).ConfigureAwait(false);

                samples.Add(Phase(attempt, timestampUtc, "ttfb", exchange.TimeToFirstByteMs));
                samples.Add(Phase(attempt, timestampUtc, "download", exchange.DownloadMs));

                ReportExchange(observer, exchange);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                samples.Add(Failed(attempt, timestampUtc, "ttfb", SampleStatus.Timeout));
            }
            catch (Exception ex)
            {
                observer.OnFact(ProbeFact.Warning("http", "Обмен", ex.Message));
                samples.Add(Failed(attempt, timestampUtc, "ttfb", SampleStatus.Error));
            }
        }

        return samples;
    }

    private static void ReportExchange(IProbeObserver observer, HttpExchange exchange)
    {
        var statusFact = exchange.StatusCode >= 400
            ? ProbeFact.Warning("http", "Код ответа", $"{exchange.StatusCode} {exchange.ReasonPhrase}")
            : ProbeFact.Text("http", "Код ответа", $"{exchange.StatusCode} {exchange.ReasonPhrase}");

        observer.OnFact(statusFact);
        observer.OnFact(ProbeFact.Text("http", "Версия", exchange.HttpVersion));
        observer.OnFact(ProbeFact.Number("http", "Размер тела", exchange.BodyBytes, MeasurementUnit.Bytes));

        foreach (var header in new[] { "server", "content-type", "location", "cache-control", "strict-transport-security" })
        {
            if (exchange.Headers.TryGetValue(header, out var value))
            {
                observer.OnFact(ProbeFact.Text("http", header, value));
            }
        }

        if (exchange.StatusCode is >= 300 and < 400 && exchange.Headers.ContainsKey("location"))
        {
            // Перенаправления не идут по цепочке намеренно: каждый переход — отдельное
            // соединение со своим водопадом, и смешивать их в одно измерение нельзя.
            observer.OnFact(ProbeFact.Text("http", "Перенаправление",
                "переходы не выполняются автоматически — измерь конечный адрес отдельно"));
        }

        var totalMs = exchange.TimeToFirstByteMs + exchange.DownloadMs;
        if (exchange.BodyBytes > 0 && exchange.DownloadMs > 0)
        {
            var mbps = exchange.BodyBytes * 8 / (exchange.DownloadMs / 1000.0) / 1_000_000.0;
            observer.OnFact(ProbeFact.Number("http", "Скорость отдачи тела", mbps, MeasurementUnit.MegabitsPerSecond));
        }

        // Именно «обмен», а не «итого»: установление соединения сюда не входит и показано
        // отдельными фазами водопада. Слово «итого» здесь противоречило бы итогу водопада.
        observer.OnFact(ProbeFact.Number("http", "Обмен (запрос → ответ)", totalMs, MeasurementUnit.Milliseconds));
    }

    private async Task<HttpExchange> PerformExchangeAsync(
        Stream stream,
        Uri uri,
        string method,
        CancellationToken cancellationToken)
    {
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        var requestText = new StringBuilder()
            .Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").Append("\r\n")
            .Append("User-Agent: StormMachine/").Append(ProductInfo.Version).Append("\r\n")
            .Append("Accept: */*\r\n")
            // Сжатие отключено намеренно: измеряем скорость канала и отдачи,
            // а не эффективность сжатия на стороне сервера.
            .Append("Accept-Encoding: identity\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        var requestBytes = Encoding.ASCII.GetBytes(requestText);

        var sentAt = _clock.GetTimestamp();
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var accumulated = new List<byte>(16 * 1024);
        var headerEnd = -1;
        double timeToFirstByteMs = 0;
        var firstByteSeen = false;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new IOException("Соединение закрыто до конца заголовков.");
            }

            if (!firstByteSeen)
            {
                timeToFirstByteMs = _clock.ElapsedMilliseconds(sentAt);
                firstByteSeen = true;
            }

            accumulated.AddRange(buffer.AsSpan(0, read));
            headerEnd = FindHeaderEnd(accumulated);

            if (accumulated.Count > 256 * 1024)
            {
                throw new IOException("Заголовки ответа неправдоподобно велики.");
            }
        }

        var downloadStart = _clock.GetTimestamp();
        var headerText = Encoding.ASCII.GetString(CollectionsMarshalSpan(accumulated, 0, headerEnd));
        var (statusCode, reason, version, headers) = ParseHead(headerText);

        var bodyBytes = (long)(accumulated.Count - (headerEnd + 4));

        if (method != "HEAD")
        {
            while (bodyBytes < MaxBodyBytes)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bodyBytes += read;
            }
        }

        return new HttpExchange
        {
            StatusCode = statusCode,
            ReasonPhrase = reason,
            HttpVersion = version,
            Headers = headers,
            BodyBytes = bodyBytes,
            TimeToFirstByteMs = timeToFirstByteMs,
            DownloadMs = _clock.ElapsedMilliseconds(downloadStart),
        };
    }

    private static ReadOnlySpan<byte> CollectionsMarshalSpan(List<byte> list, int start, int length) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(start, length);

    private static int FindHeaderEnd(List<byte> data)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);

        for (var i = 0; i + 3 < span.Length; i++)
        {
            if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n'
                && span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static (int StatusCode, string Reason, string Version, Dictionary<string, string> Headers) ParseHead(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (lines.Length == 0)
        {
            return (0, "нет статусной строки", "?", headers);
        }

        var statusParts = lines[0].Split(' ', 3);
        var version = statusParts.Length > 0 ? statusParts[0] : "?";
        var code = statusParts.Length > 1 && int.TryParse(statusParts[1], CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var reason = statusParts.Length > 2 ? statusParts[2] : string.Empty;

        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = lines[i][..separator].Trim();
            var value = lines[i][(separator + 1)..].Trim();
            headers[name] = value;
        }

        return (code, reason, version, headers);
    }

    private static Uri BuildUri(Target target)
    {
        var raw = target.Value;

        if (target.Kind == TargetKind.Url)
        {
            return new Uri(raw);
        }

        return raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(raw)
            : new Uri($"https://{raw}");
    }

    private static Sample Phase(int attempt, DateTimeOffset timestampUtc, string label, double value) => new()
    {
        Sequence = attempt,
        TimestampUtc = timestampUtc,
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = attempt,
    };

    private static Sample Failed(int attempt, DateTimeOffset timestampUtc, string label, SampleStatus status) =>
        Sample.Failed(attempt, timestampUtc, status) with { Label = label, Group = attempt };

    private sealed record HttpExchange
    {
        public required int StatusCode { get; init; }

        public required string ReasonPhrase { get; init; }

        public required string HttpVersion { get; init; }

        public required Dictionary<string, string> Headers { get; init; }

        public required long BodyBytes { get; init; }

        public required double TimeToFirstByteMs { get; init; }

        public required double DownloadMs { get; init; }
    }
}
