using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Domain.Topology;

namespace StormMachine.Application.Topology;

/// <summary>
/// Выгрузка карты в JSON.
/// </summary>
/// <remarks>
/// Формат намеренно плоский — узлы и связи двумя списками. Он читается человеком,
/// разбирается любым инструментом и не привязан к нашему движку раскладки: координаты
/// в него не входят, потому что расположение узлов не свойство сети, а свойство показа.
/// </remarks>
public static class TopologyDocumentJson
{
    /// <summary>
    /// Контекст сериализации, построенный на исходниках, а не на рефлексии.
    /// </summary>
    /// <remarks>
    /// Клиенты публикуются с обрезкой неиспользуемого кода, и рефлексивная сериализация
    /// при обрезке ломается — причём не при сборке, а при первом обращении у пользователя.
    /// Настройки передаются самому контексту: иначе пришлось бы вызывать перегрузку,
    /// которую анализатор обрезки справедливо запрещает.
    /// </remarks>
    private static readonly TopologyJsonContext Context = new(new JsonSerializerOptions(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // Кириллица в подписях узлов и в объяснениях связей должна оставаться читаемой:
        // выгрузку открывают глазами, а не только разбирают программой.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string Serialize(TopologyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return JsonSerializer.Serialize(
            new TopologyDocument
            {
                Nodes = [.. graph.Nodes.Select(TopologyNodeDto.Of)],
                Links = [.. graph.Links.Select(TopologyLinkDto.Of)],
            },
            Context.TopologyDocument);
    }
}

/// <summary>Карта целиком.</summary>
public sealed record TopologyDocument
{
    public required IReadOnlyList<TopologyNodeDto> Nodes { get; init; }

    public required IReadOnlyList<TopologyLinkDto> Links { get; init; }
}

public sealed record TopologyNodeDto
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Label { get; init; }

    public string? Address { get; init; }

    public string? MacAddress { get; init; }

    public string? Vendor { get; init; }

    public int? GroupSize { get; init; }

    public bool IsOnline { get; init; }

    public string? Detail { get; init; }

    public static TopologyNodeDto Of(TopologyNode node) => new()
    {
        Id = node.Id,
        Kind = node.Kind.ToString(),
        Label = node.Label,
        Address = node.Address,
        MacAddress = node.MacAddress,
        Vendor = node.Vendor,
        GroupSize = node.GroupSize > 0 ? node.GroupSize : null,
        IsOnline = node.IsOnline,
        Detail = node.Detail,
    };
}

public sealed record TopologyLinkDto
{
    public required string From { get; init; }

    public required string To { get; init; }

    public required string Kind { get; init; }

    public required string Confidence { get; init; }

    /// <summary>Почему связь нарисована — выгружается вместе со связью.</summary>
    public required string Because { get; init; }

    public static TopologyLinkDto Of(TopologyLink link) => new()
    {
        From = link.From,
        To = link.To,
        Kind = link.Kind.ToString(),
        Confidence = link.Confidence.ToString(),
        Because = link.Because,
    };
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(TopologyDocument))]
internal sealed partial class TopologyJsonContext : JsonSerializerContext
{
}
