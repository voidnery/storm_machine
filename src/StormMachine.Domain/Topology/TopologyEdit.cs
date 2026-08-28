namespace StormMachine.Domain.Topology;

/// <summary>Что оператор поправил на карте.</summary>
public enum TopologyEditKind
{
    /// <summary>Связь, которую инструмент не увидел, а оператор знает.</summary>
    AddLink,

    /// <summary>Связь, которую инструмент вывел ошибочно.</summary>
    RemoveLink,

    /// <summary>Узел, который на карте не нужен.</summary>
    HideNode,
}

/// <summary>
/// Правка карты, сделанная оператором.
/// </summary>
/// <remarks>
/// Хранится не результат правки, а сама правка — как свидетельство с наивысшим весом.
/// Разница принципиальная: граф пересчитывается из свидетельств при каждом сканировании,
/// и правка, записанная в результат, была бы затёрта первым же пересчётом. Записанная
/// как свидетельство — переживает любое их число.
/// <para>
/// Отсюда же следует, что правку можно отменить, не трогая наблюдения: удаляется одна
/// запись, и карта возвращается к тому, что видит инструмент.
/// </para>
/// </remarks>
public sealed record TopologyEdit
{
    public required Guid Id { get; init; }

    public required TopologyEditKind Kind { get; init; }

    /// <summary>Узел, к которому относится правка.</summary>
    public required string Subject { get; init; }

    /// <summary>Второй конец связи. Для скрытия узла не используется.</summary>
    public string? Target { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required string Operator { get; init; }

    /// <summary>Почему оператор так решил — попадает в подпись связи на карте.</summary>
    public string? Note { get; init; }

    public static TopologyEdit Link(string from, string to, string author, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TopologyEditKind.AddLink,
        Subject = from,
        Target = to,
        AtUtc = DateTimeOffset.UtcNow,
        Operator = author,
        Note = note,
    };

    public static TopologyEdit Unlink(string from, string to, string author, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TopologyEditKind.RemoveLink,
        Subject = from,
        Target = to,
        AtUtc = DateTimeOffset.UtcNow,
        Operator = author,
        Note = note,
    };

    public static TopologyEdit Hide(string node, string author, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TopologyEditKind.HideNode,
        Subject = node,
        AtUtc = DateTimeOffset.UtcNow,
        Operator = author,
        Note = note,
    };

    /// <summary>Описание правки для журнала и для подсказки.</summary>
    public string Describe() => Kind switch
    {
        TopologyEditKind.AddLink => $"связь {Subject} — {Target} добавлена оператором",
        TopologyEditKind.RemoveLink => $"связь {Subject} — {Target} убрана оператором",
        _ => $"узел {Subject} скрыт оператором",
    };
}

/// <summary>
/// Объединение двух записей в одно устройство.
/// </summary>
/// <remarks>
/// Нужно там, где одна железка видна инструменту дважды: ноутбук с проводом и Wi-Fi
/// даёт два MAC, гипервизор — свой адрес и адрес виртуального коммутатора. Инструмент
/// не может знать, что это одно устройство: наблюдения у него разные и одинаково
/// достоверные. Знает оператор.
/// <para>
/// Объединение живёт в инвентаре, а не на карте, и это не деталь реализации. Устройство
/// одно во всём продукте: в списке, в различиях между сканами, на карте. Объединив дубли
/// в одном месте, оператор не должен объединять их ещё где-то.
/// </para>
/// </remarks>
public sealed record DeviceAlias
{
    /// <summary>Тождество, которое перестаёт быть отдельным устройством.</summary>
    public required string Alias { get; init; }

    /// <summary>Тождество, к которому оно присоединяется.</summary>
    public required string Primary { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required string Operator { get; init; }
}
