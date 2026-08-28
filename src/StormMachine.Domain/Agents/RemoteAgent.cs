namespace StormMachine.Domain.Agents;

/// <summary>Как клиент соединяется с агентом.</summary>
public enum AgentDirection
{
    /// <summary>Клиент звонит агенту. Требует разрешённых входящих на машине агента.</summary>
    ClientDials = 1,

    /// <summary>Агент звонит клиенту. Прав на машине агента не требует.</summary>
    AgentDials = 2,
}

/// <summary>
/// Сопряжённый агент.
/// </summary>
/// <remarks>
/// Отпечаток — единственное, что делает агента этим агентом. Имя машины и адрес меняются
/// (DHCP, переименование, переезд), а отпечаток нет: он и есть личность. Поэтому ключ
/// хранения — отпечаток, а не адрес, и агент, сменивший адрес, остаётся тем же агентом.
/// <para>
/// Направление хранится вместе с агентом, потому что оно свойство площадки, а не разовый
/// выбор: если на площадке нет прав открыть входящий порт, это верно и завтра.
/// </para>
/// </remarks>
public sealed record RemoteAgent
{
    public required string Thumbprint { get; init; }

    public required string MachineName { get; init; }

    public required string Product { get; init; }

    /// <summary>Адрес, по которому агент отвечал в прошлый раз. Для дозвона клиентом.</summary>
    public string? Address { get; init; }

    public int Port { get; init; }

    public required AgentDirection Direction { get; init; }

    public required DateTimeOffset PairedUtc { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }

    /// <summary>Что агент умеет — по его собственному заявлению при рукопожатии.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Как агента называть в списке: имя оператора, если задано, иначе имя машины.</summary>
    public string? Alias { get; init; }

    public string DisplayName => Alias is { Length: > 0 } alias ? alias : MachineName;

    /// <summary>Короткий отпечаток для показа: полный не помещается и не читается.</summary>
    public string ShortThumbprint => Thumbprint.Length >= 8 ? Thumbprint[..8] : Thumbprint;

    /// <summary>Отпечаток группами по четыре знака: его сверяют глазами и читают вслух.</summary>
    public string GroupedThumbprint => Group(Thumbprint);

    /// <summary>
    /// Разбивает отпечаток на читаемые группы.
    /// </summary>
    /// <remarks>
    /// Такое же правило есть в слое протокола — и это не упущение. Протоколу запрещено
    /// зависеть от доменной модели: агент собирается отдельным маленьким бинарём и
    /// доменной модели не содержит вовсе. Единственная альтернатива — заставить показ
    /// зависеть от провода, а это дороже пяти строк.
    /// </remarks>
    public static string Group(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);

        var parts = new List<string>((thumbprint.Length / 4) + 1);

        for (var at = 0; at < thumbprint.Length; at += 4)
        {
            parts.Add(thumbprint.Substring(at, Math.Min(4, thumbprint.Length - at)));
        }

        return string.Join(' ', parts);
    }

    public string DescribeDirection() => Direction switch
    {
        AgentDirection.ClientDials => Address is { Length: > 0 } address
            ? $"звоним мы, на {address}:{Port}"
            : "звоним мы",
        _ => "звонит агент",
    };
}
