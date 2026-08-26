using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Domain.Measurements;

namespace StormMachine.Storage;

/// <summary>
/// Сериализация того, что хранится в JSON-полях.
/// </summary>
/// <remarks>
/// В колонки вынесено только то, по чему ищут и сортируют. Остальное — условия измерения,
/// факты, параметры пробы — лежит в JSON. Причина в И-2: шесть проб дали четыре формы
/// результата, и попытка разложить специфику каждой по колонкам превратила бы схему
/// в набор почти пустых полей.
/// <para>
/// Контекст сгенерирован исходниками, а не построен рефлексией: клиенты публикуются
/// с обрезкой неиспользуемого кода, а рефлексивная сериализация при обрезке ломается —
/// причём не при сборке, а при первом обращении у пользователя.
/// </para>
/// </remarks>
internal static class StorageJson
{
    public static string SerializeContext(MeasurementContext value) =>
        JsonSerializer.Serialize(value, StorageJsonContext.Default.MeasurementContext);

    public static MeasurementContext? DeserializeContext(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, StorageJsonContext.Default.MeasurementContext);

    public static string SerializeFacts(ProbeFact[] value) =>
        JsonSerializer.Serialize(value, StorageJsonContext.Default.ProbeFactArray);

    public static ProbeFact[] DeserializeFacts(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, StorageJsonContext.Default.ProbeFactArray) ?? [];

    public static string SerializeParameters(Dictionary<string, string?> value) =>
        JsonSerializer.Serialize(value, StorageJsonContext.Default.DictionaryStringString);

    public static Dictionary<string, string?> DeserializeParameters(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, StorageJsonContext.Default.DictionaryStringString) ?? [];
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeasurementContext))]
[JsonSerializable(typeof(ProbeFact[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
