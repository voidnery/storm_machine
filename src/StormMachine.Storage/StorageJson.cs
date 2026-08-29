using System.Globalization;
using StormMachine.Domain.Targets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Scenarios;

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
/// с обрезкой неиспользуемого кода, а рефлексивная сериализация при обрезке ломается.
/// Важна и форма вызова: перегрузка с <c>JsonSerializerOptions</c> помечена как
/// несовместимая с обрезкой, и публикация с ней не собирается вовсе. Нужна перегрузка
/// с <c>JsonTypeInfo</c> из контекста — настройки при этом задаются самому контексту.
/// </para>
/// </remarks>
/// <summary>
/// Шаг сценария в виде, пригодном для хранения.
/// </summary>
/// <remarks>
/// Отдельный тип, потому что у шага параметры объявлены как <c>object?</c> — их типы
/// знает проба, а не сценарий. Под обрезкой такой словарь не сериализуется вовсе:
/// генератор исходников не может знать, что окажется внутри, и падает на первом же
/// значении. Найдено запуском в И-22, а не рассуждением.
/// <para>
/// Хранятся строками — ровно как параметры пресета и монитора, и по той же причине:
/// их набор задаёт проба своим объявлением, а разбор в нужный тип делает она же
/// при запуске.
/// </para>
/// </remarks>
internal sealed record StoredStep(
    string Name,
    string ProbeName,
    TargetKind TargetKind,
    string TargetValue,
    string? TargetLabel,
    Dictionary<string, string?> Parameters,
    Threshold[] Thresholds,
    string PhaseMetric,
    bool ContinueOnFailure);

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
    private static readonly StorageJsonContext Context = new(new JsonSerializerOptions(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string SerializeContext(MeasurementContext value) =>
        JsonSerializer.Serialize(value, Context.MeasurementContext);

    public static MeasurementContext? DeserializeContext(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, Context.MeasurementContext);

    public static string SerializeFacts(ProbeFact[] value) =>
        JsonSerializer.Serialize(value, Context.ProbeFactArray);

    public static ProbeFact[] DeserializeFacts(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, Context.ProbeFactArray) ?? [];

    public static string SerializeTags(string[] value) =>
        JsonSerializer.Serialize(value, Context.StringArray);

    public static string[] DeserializeTags(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, Context.StringArray) ?? [];

    public static string SerializeEvidence(Evidence[] value) =>
        JsonSerializer.Serialize(value, Context.EvidenceArray);

    public static Evidence[] DeserializeEvidence(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, Context.EvidenceArray) ?? [];

    public static string SerializeSchedule(Schedule value) =>
        JsonSerializer.Serialize(value, Context.Schedule);

    public static Schedule? DeserializeSchedule(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, Context.Schedule);

    public static string SerializeSteps(ScenarioStep[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var stored = value
            .Select(s => new StoredStep(
                s.Name,
                s.ProbeName,
                s.Target.Kind,
                s.Target.Value,
                s.Target.Label,
                s.Parameters.ToDictionary(
                    p => p.Key,
                    p => p.Value switch
                    {
                        null => null,
                        bool flag => flag ? "true" : "false",
                        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                        var other => other.ToString(),
                    },
                    StringComparer.OrdinalIgnoreCase),
                [.. s.Thresholds],
                s.PhaseMetric,
                s.ContinueOnFailure))
            .ToArray();

        return JsonSerializer.Serialize(stored, Context.StoredStepArray);
    }

    public static ScenarioStep[] DeserializeSteps(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        var stored = JsonSerializer.Deserialize(json, Context.StoredStepArray) ?? [];

        return [.. stored.Select(s => new ScenarioStep
        {
            Name = s.Name,
            ProbeName = s.ProbeName,
            Target = new Target { Kind = s.TargetKind, Value = s.TargetValue, Label = s.TargetLabel },
            Parameters = s.Parameters.ToDictionary(
                p => p.Key,
                p => (object?)p.Value,
                StringComparer.OrdinalIgnoreCase),
            Thresholds = s.Thresholds,

            // Поле с умолчанием: у шага, сохранённого до появления колонки, оно пусто,
            // и подставить сюда пустую строку значило бы сломать разбивку по фазам.
            PhaseMetric = string.IsNullOrWhiteSpace(s.PhaseMetric) ? "p50" : s.PhaseMetric,

            // Терять это поле нельзя: оно решает, идёт ли сценарий дальше после отказа.
            // Пропажа изменила бы поведение молча — россыпь отказов вместо одного
            // внятного «сломалось здесь».
            ContinueOnFailure = s.ContinueOnFailure,
        })];
    }

    public static string SerializeThresholds(Threshold[] value) =>
        JsonSerializer.Serialize(value, Context.ThresholdArray);

    public static Threshold[] DeserializeThresholds(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize(json, Context.ThresholdArray) ?? [];

    public static string? SerializeAlertRule(AlertRule? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Context.AlertRule);

    public static AlertRule? DeserializeAlertRule(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, Context.AlertRule);

    public static string? SerializeObjective(ServiceLevelObjective? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Context.ServiceLevelObjective);

    public static ServiceLevelObjective? DeserializeObjective(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, Context.ServiceLevelObjective);

    public static string SerializeAlertState(AlertState value) =>
        JsonSerializer.Serialize(value, Context.AlertState);

    public static AlertState DeserializeAlertState(string? json) =>
        string.IsNullOrEmpty(json)
            ? AlertState.Clear
            : JsonSerializer.Deserialize(json, Context.AlertState) ?? AlertState.Clear;

    public static string SerializeBaselineMetrics(BaselineMetric[] value) =>
        JsonSerializer.Serialize(value, Context.BaselineMetricArray);

    public static BaselineMetric[] DeserializeBaselineMetrics(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize(json, Context.BaselineMetricArray) ?? [];

    public static string SerializeSignature(NetworkSignature value) =>
        JsonSerializer.Serialize(value, Context.NetworkSignature);

    public static NetworkSignature DeserializeSignature(string? json) =>
        string.IsNullOrEmpty(json)
            ? new NetworkSignature()
            : JsonSerializer.Deserialize(json, Context.NetworkSignature) ?? new NetworkSignature();

    public static string SerializeParameters(Dictionary<string, string?> value) =>
        JsonSerializer.Serialize(value, Context.DictionaryStringString);

    public static Dictionary<string, string?> DeserializeParameters(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, Context.DictionaryStringString) ?? [];
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeasurementContext))]
[JsonSerializable(typeof(ProbeFact[]))]
[JsonSerializable(typeof(Evidence[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Schedule))]
[JsonSerializable(typeof(Threshold[]))]
[JsonSerializable(typeof(AlertRule))]
[JsonSerializable(typeof(AlertState))]
[JsonSerializable(typeof(ServiceLevelObjective))]
[JsonSerializable(typeof(BaselineMetric[]))]
[JsonSerializable(typeof(NetworkSignature))]
[JsonSerializable(typeof(StoredStep[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
