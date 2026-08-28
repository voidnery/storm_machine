namespace StormMachine.Domain.Outside;

/// <summary>
/// Поведение NAT при отображении (RFC 4787 §4.1).
/// </summary>
/// <remarks>
/// Терминология из RFC, а не привычные «полный конус», «симметричный» из RFC 3489.
/// Старая классификация отозвана самим же IETF как неоднозначная: она смешивала
/// отображение с фильтрацией, и одно и то же устройство разные инструменты называли
/// по-разному. Здесь названо только то, что действительно измерено.
/// </remarks>
public enum NatMapping
{
    /// <summary>Ни один сервер не ответил — сказать нечего.</summary>
    Unknown,

    /// <summary>Трансляции нет: снаружи машина видна тем же адресом и портом.</summary>
    None,

    /// <summary>Один и тот же порт для разных адресатов. Прямое соединение обычно устанавливается.</summary>
    EndpointIndependent,

    /// <summary>Каждому адресату свой порт. Прямое соединение потребует ретрансляции.</summary>
    AddressDependent,

    /// <summary>Ответил только один сервер: трансляция видна, её поведение — нет.</summary>
    Undetermined,
}

/// <summary>Готовность к IPv6 — три независимых условия, каждое из которых обязательно.</summary>
public sealed record Ipv6Readiness
{
    /// <summary>У машины есть глобальный адрес IPv6.</summary>
    public required bool HasGlobalAddress { get; init; }

    public string? GlobalAddress { get; init; }

    /// <summary>Имя цели разрешается в адрес IPv6.</summary>
    public required bool ResolvesAaaa { get; init; }

    public string? AaaaAddress { get; init; }

    /// <summary>До цели по IPv6 удалось установить соединение.</summary>
    public required bool Reachable { get; init; }

    public string? Failure { get; init; }

    public bool IsReady => HasGlobalAddress && ResolvesAaaa && Reachable;

    /// <summary>
    /// Где именно обрывается готовность.
    /// </summary>
    /// <remarks>
    /// Три условия названы по отдельности не для полноты. «IPv6 не работает» — бесполезный
    /// вывод: нет адреса — вопрос к провайдеру, нет AAAA — вопрос к владельцу имени,
    /// есть и то и другое, но нет связности — вопрос к маршрутизации или к фильтру.
    /// Это три разных разговора с тремя разными людьми.
    /// </remarks>
    public string Describe()
    {
        if (IsReady)
        {
            return "готова: есть глобальный адрес, имя разрешается в AAAA, соединение устанавливается";
        }

        if (!HasGlobalAddress)
        {
            return "нет глобального адреса IPv6 — провайдер или маршрутизатор его не выдал";
        }

        if (!ResolvesAaaa)
        {
            return "адрес есть, но у цели нет записи AAAA — до неё по IPv6 идти некуда";
        }

        return Failure is { Length: > 0 } failure
            ? $"адрес и AAAA есть, но соединение не устанавливается: {failure}"
            : "адрес и AAAA есть, но соединение не устанавливается";
    }
}

/// <summary>Ответ одного сервера STUN — какими адресом и портом он увидел машину.</summary>
public sealed record OutsideMapping(string Server, string? Address, int Port, string? Failure)
{
    public bool Answered => Address is not null;
}

/// <summary>
/// Как сеть видна снаружи.
/// </summary>
/// <remarks>
/// Вопрос, на который изнутри сети не отвечает ни одна проба. Внешний адрес, поведение
/// трансляции и готовность к IPv6 определяются только тем, что скажет собеседник за
/// пределами NAT, — и именно поэтому эта проверка требует обращения к чужим серверам
/// и не выполняется сама собой.
/// </remarks>
public sealed record OutsideView
{
    public string? LocalAddress { get; init; }

    public int LocalPort { get; init; }

    public string? ExternalAddress { get; init; }

    public int ExternalPort { get; init; }

    public required NatMapping Mapping { get; init; }

    public IReadOnlyList<OutsideMapping> Mappings { get; init; } = [];

    /// <summary>Имя внешнего адреса из обратной зоны DNS.</summary>
    public string? HostName { get; init; }

    public int? AsNumber { get; init; }

    public string? AsOrganization { get; init; }

    public string? Country { get; init; }

    /// <summary>Указание источника данных о принадлежности — требование лицензии базы.</summary>
    public string? Attribution { get; init; }

    public Ipv6Readiness? Ipv6 { get; init; }

    /// <summary>Чего выяснить не удалось и почему. Пустое молчание хуже отрицательного ответа.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsBehindNat => Mapping is NatMapping.EndpointIndependent
        or NatMapping.AddressDependent
        or NatMapping.Undetermined;

    public string DescribeMapping() => Mapping switch
    {
        NatMapping.None =>
            "трансляции нет — машина видна снаружи своим адресом",
        NatMapping.EndpointIndependent =>
            "есть, отображение не зависит от адресата: разным серверам машина видна одним и тем же портом. "
            + "Прямое соединение между узлами обычно устанавливается",
        NatMapping.AddressDependent =>
            "есть, отображение зависит от адресата: каждому серверу машина видна своим портом. "
            + "Прямое соединение между узлами не устанавливается — потребуется ретрансляция (TURN)",
        NatMapping.Undetermined =>
            "есть, но поведение не определено: ответил только один сервер, а для вывода нужны два",
        _ => "не определена: ни один сервер STUN не ответил",
    };

    /// <summary>Чего эта проверка не выясняет — говорится всегда, чтобы не додумывали.</summary>
    public const string FilteringNotTested =
        "Поведение при фильтрации (кого NAT пускает обратно) не проверялось: для этого нужен "
        + "запрос CHANGE-REQUEST из RFC 5780, который поддерживают не все публичные серверы.";
}
