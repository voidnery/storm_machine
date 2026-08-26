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
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        TypeInfoResolver = PresetJsonContext.Default,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Без этого имя «Шлюз — быстрая проверка» превращается в вереницу Шл…
        // и файл перестаёт быть человекочитаемым, ради чего он и задуман. Название
        // кодировщика пугает, но относится к вставке JSON в HTML — здесь это локальный файл.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(PresetBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize<PresetBundle>(bundle, Options);
    }

    public static PresetBundle Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var bundle = JsonSerializer.Deserialize<PresetBundle>(json, Options);

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
