namespace StormMachine.Domain.Targets;

/// <summary>Что именно измеряем.</summary>
public enum TargetKind
{
    /// <summary>Явный IP-адрес: 192.168.1.1</summary>
    IpAddress,

    /// <summary>Имя узла: server01, example.com</summary>
    Hostname,

    /// <summary>Полный URL: https://example.com/health</summary>
    Url,

    /// <summary>Подсеть в нотации CIDR: 192.168.1.0/24</summary>
    Subnet,

    /// <summary>Шлюз по умолчанию — разрешается в момент выполнения.</summary>
    DefaultGateway,

    /// <summary>Внешний IP-адрес — разрешается в момент выполнения.</summary>
    ExternalIp,
}

/// <summary>
/// Цель измерения. Неизменяемая: цель, попавшая в прогон, больше не меняется,
/// иначе результаты нельзя было бы сопоставлять между запусками.
/// </summary>
/// <remarks>
/// Динамические цели (<see cref="TargetKind.DefaultGateway"/>, <see cref="TargetKind.ExternalIp"/>)
/// хранят намерение, а не адрес. Адрес разрешается при выполнении и попадает в результат —
/// так пресет «пинговать шлюз» остаётся осмысленным в любой сети.
/// </remarks>
public sealed record Target
{
    public required TargetKind Kind { get; init; }

    /// <summary>Исходное значение в том виде, в каком его задал оператор.</summary>
    public required string Value { get; init; }

    /// <summary>Понятное человеку имя. Если не задано — используется <see cref="Value"/>.</summary>
    public string? Label { get; init; }

    public string DisplayName => Label ?? Value;

    public static Target Ip(string address, string? label = null) =>
        new() { Kind = TargetKind.IpAddress, Value = address, Label = label };

    public static Target Host(string hostname, string? label = null) =>
        new() { Kind = TargetKind.Hostname, Value = hostname, Label = label };

    public static Target Url(string url, string? label = null) =>
        new() { Kind = TargetKind.Url, Value = url, Label = label };

    public static Target Subnet(string cidr, string? label = null) =>
        new() { Kind = TargetKind.Subnet, Value = cidr, Label = label };

    public static Target Gateway(string? label = null) =>
        new() { Kind = TargetKind.DefaultGateway, Value = "<gateway>", Label = label };

    public static Target ExternalIp(string? label = null) =>
        new() { Kind = TargetKind.ExternalIp, Value = "<external-ip>", Label = label };

    /// <summary>Разбирает строку оператора в цель, определяя вид по форме записи.</summary>
    public static Target Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var value = raw.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Url(value);
        }

        if (value.Contains('/', StringComparison.Ordinal))
        {
            return Subnet(value);
        }

        return System.Net.IPAddress.TryParse(value, out _) ? Ip(value) : Host(value);
    }

    public override string ToString() => DisplayName;
}
