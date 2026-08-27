using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Domain.Presets;

namespace StormMachine.Application.Presets;

/// <summary>
/// Чтение и запись файла обмена пресетами.
/// </summary>
/// <remarks>
/// Формат человекочитаемый: файл с пресетами оператор может открыть, поправить и отдать
/// коллеге. Отступы включены намеренно — экономия байт здесь ничего не стоит, а
/// возможность заглянуть внутрь стоит многого.
/// <para>
/// Контекст сгенерирован исходниками: клиенты публикуются с обрезкой, и рефлексивная
/// сериализация сломалась бы у пользователя, а не при сборке.
/// </para>
/// </remarks>
public static class PresetBundleJson
{
    /// <summary>
    /// Контекст с настройками — вместо пары «настройки и резолвер».
    /// </summary>
    /// <remarks>
    /// Разница не косметическая. Перегрузка, принимающая <c>JsonSerializerOptions</c>,
    /// помечена как несовместимая с обрезкой, и публикация клиента с ней не собирается
    /// вовсе. Перегрузка, принимающая <c>JsonTypeInfo</c> из контекста, обрезку
    /// переживает — а настройки при этом задаются самому контексту.
    /// </remarks>
    private static readonly PresetJsonContext Context = new(new JsonSerializerOptions(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Без этого имя «Шлюз — быстрая проверка» превращается в вереницу Шл…
        // и файл перестаёт быть человекочитаемым, ради чего он и задуман. Название
        // кодировщика пугает, но относится к вставке JSON в HTML — здесь это локальный файл.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string Write(PresetBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, Context.PresetBundle);
    }

    public static PresetBundle Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var bundle = JsonSerializer.Deserialize(json, Context.PresetBundle);

        return bundle
               ?? throw new FormatException("Файл пуст или не является набором пресетов Storm Machine.");
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PresetBundle))]
internal sealed partial class PresetJsonContext : JsonSerializerContext
{
}
