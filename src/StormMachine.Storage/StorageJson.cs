using System.Text.Encodings.Web;
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
    /// <summary>
    /// Кодировщик, не экранирующий кириллицу.
    /// </summary>
    /// <remarks>
    /// Без него имя «сеть» уезжает в базу как <c>сеть</c>. Это не только
    /// делает содержимое нечитаемым при отладке, но и ломает поиск: сравнение подстроки
    /// в SQL перестаёт находить то, что человек ввёл. Название кодировщика пугает,
    /// но относится к вставке JSON в HTML — здесь это локальный файл базы.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        TypeInfoResolver = StorageJsonContext.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeContext(MeasurementContext value) =>
        JsonSerializer.Serialize<MeasurementContext>(value, Options);

    public static MeasurementContext? DeserializeContext(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<MeasurementContext>(json, Options);

    public static string SerializeFacts(ProbeFact[] value) =>
        JsonSerializer.Serialize<ProbeFact[]>(value, Options);

    public static ProbeFact[] DeserializeFacts(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<ProbeFact[]>(json, Options) ?? [];

    public static string SerializeTags(string[] value) =>
        JsonSerializer.Serialize<string[]>(value, Options);

    public static string[] DeserializeTags(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<string[]>(json, Options) ?? [];

    public static string SerializeParameters(Dictionary<string, string?> value) =>
        JsonSerializer.Serialize<Dictionary<string, string?>>(value, Options);

    public static Dictionary<string, string?> DeserializeParameters(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options) ?? [];
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeasurementContext))]
[JsonSerializable(typeof(ProbeFact[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
