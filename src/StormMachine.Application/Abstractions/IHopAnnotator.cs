namespace StormMachine.Application.Abstractions;

/// <summary>Что удалось узнать про узел маршрута сверх его адреса.</summary>
public sealed record HopAnnotation
{
    /// <summary>Категория фактов, в которой проба публикует таблицу «адрес → чем известен».</summary>
    public const string FactCategory = "route";

    /// <summary>Подпись частного адреса — по ней показ отличает «нечего сказать» от «не узнали».</summary>
    public const string PrivateLabel = "частный адрес";

    public required string Address { get; init; }

    /// <summary>Имя из обратной зоны DNS.</summary>
    public string? HostName { get; init; }

    /// <summary>Номер автономной системы.</summary>
    public int? AsNumber { get; init; }

    /// <summary>Владелец автономной системы.</summary>
    public string? AsOrganization { get; init; }

    /// <summary>Страна по базе геолокации.</summary>
    public string? Country { get; init; }

    /// <summary>Адрес из частного диапазона: своя сеть, аннотировать нечем и незачем.</summary>
    public bool IsPrivate { get; init; }

    public bool HasAnything => HostName is not null || AsNumber is not null || Country is not null;

    /// <summary>Короткая подпись для таблицы маршрута.</summary>
    public string Describe()
    {
        if (IsPrivate)
        {
            return PrivateLabel;
        }

        var parts = new List<string>(3);

        if (HostName is not null)
        {
            parts.Add(HostName);
        }

        if (AsNumber is { } asn)
        {
            parts.Add(AsOrganization is null ? $"AS{asn}" : $"AS{asn} {AsOrganization}");
        }

        if (Country is not null)
        {
            parts.Add(Country);
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }
}

/// <summary>
/// Обогащение узлов маршрута: имена и принадлежность к автономным системам.
/// </summary>
/// <remarks>
/// Голый список адресов отвечает на вопрос «где», но не на вопрос «у кого». Понимание,
/// что потери начинаются в транзите конкретного оператора, и есть то, ради чего
/// трассировку показывают провайдеру.
/// <para>
/// Данные ASN требуют офлайн-базы, которой может не быть. Обогащение обязано деградировать
/// молча: без базы остаются адреса и имена, и это по-прежнему полезно.
/// </para>
/// </remarks>
public interface IHopAnnotator
{
    /// <summary>Доступны ли данные об автономных системах.</summary>
    bool HasAsnData { get; }

    /// <summary>Где инструмент ищет базу — показывается оператору, если её нет.</summary>
    string AsnDatabaseHint { get; }

    /// <summary>
    /// Обязательное указание источника данных. <c>null</c> — данных нет, указывать нечего.
    /// </summary>
    /// <remarks>
    /// Не формальность: лицензия базы принадлежности требует называть источник везде,
    /// где показан результат. Поэтому строка приходит из порта, а не зашита в отчёт.
    /// </remarks>
    string? Attribution { get; }

    Task<IReadOnlyDictionary<string, HopAnnotation>> AnnotateAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default);
}
