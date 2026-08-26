namespace StormMachine.Domain.Measurements;

/// <summary>
/// Методика измерения. Обязательная часть результата: отчёт со ссылкой на стандарт —
/// аргумент в споре с провайдером, отчёт без методики — просто картинка
/// (требование C-08a, docs/01-analysis.md §6).
/// </summary>
public sealed record Methodology
{
    public required string Name { get; init; }

    /// <summary>Ссылка на стандарт: «RFC 3550 §6.4.1», «ITU-T G.107».</summary>
    public required string Reference { get; init; }

    public string? Url { get; init; }

    public override string ToString() => $"{Name} ({Reference})";

    public static readonly Methodology IcmpEcho = new()
    {
        Name = "ICMP Echo",
        Reference = "RFC 792",
        Url = "https://www.rfc-editor.org/rfc/rfc792",
    };

    public static readonly Methodology InterarrivalJitter = new()
    {
        Name = "Interarrival jitter",
        Reference = "RFC 3550 §6.4.1",
        Url = "https://www.rfc-editor.org/rfc/rfc3550#section-6.4.1",
    };

    public static readonly Methodology TcpThroughput = new()
    {
        Name = "TCP throughput",
        Reference = "RFC 6349",
        Url = "https://www.rfc-editor.org/rfc/rfc6349",
    };

    public static readonly Methodology EModelMos = new()
    {
        Name = "E-model, R-фактор и MOS",
        Reference = "ITU-T G.107",
        Url = "https://www.itu.int/rec/T-REC-G.107",
    };

    public static readonly Methodology PathMtuDiscovery = new()
    {
        Name = "Path MTU Discovery",
        Reference = "RFC 1191",
        Url = "https://www.rfc-editor.org/rfc/rfc1191",
    };

    public static readonly Methodology TcpConnectLatency = new()
    {
        Name = "TCP connect latency",
        Reference = "собственная методика: время до завершения трёхстороннего рукопожатия",
    };

    public static readonly Methodology TcpConnect = TcpConnectLatency;

    public static readonly Methodology UdpProbe = new()
    {
        Name = "UDP-проба",
        Reference = "RFC 768; молчание не равно недоступности",
        Url = "https://www.rfc-editor.org/rfc/rfc768",
    };

    public static readonly Methodology DnsQuery = new()
    {
        Name = "DNS-запрос",
        Reference = "RFC 1035",
        Url = "https://www.rfc-editor.org/rfc/rfc1035",
    };

    public static readonly Methodology TlsHandshake = new()
    {
        Name = "Рукопожатие TLS",
        Reference = "RFC 8446 (TLS 1.3), RFC 5246 (TLS 1.2)",
        Url = "https://www.rfc-editor.org/rfc/rfc8446",
    };

    public static readonly Methodology HttpTiming = new()
    {
        Name = "Разбивка времени HTTP по фазам",
        Reference = "RFC 9110; фазы: DNS, TCP, TLS, первый байт, скачивание",
        Url = "https://www.rfc-editor.org/rfc/rfc9110",
    };

    public static readonly Methodology Traceroute = new()
    {
        Name = "Traceroute по TTL",
        Reference = "RFC 792; ICMP Time Exceeded",
        Url = "https://www.rfc-editor.org/rfc/rfc792",
    };

    public static readonly Methodology Unspecified = new()
    {
        Name = "Не указана",
        Reference = "—",
    };
}
