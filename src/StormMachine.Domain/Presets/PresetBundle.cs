namespace StormMachine.Domain.Presets;

/// <summary>
/// Пресет в переносимом виде.
/// </summary>
/// <remarks>
/// Идентификатор и счётчики запусков сюда не входят: при переносе на другую машину
/// они бессмысленны, а совпадение идентификаторов породило бы ложное ощущение, что это
/// один и тот же объект. Переносится замысел теста, а не его история.
/// </remarks>
public sealed record PortablePreset
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string ProbeName { get; init; }

    public required string TargetKind { get; init; }

    public required string TargetValue { get; init; }

    public string? TargetLabel { get; init; }

    public required Dictionary<string, string?> Parameters { get; init; }

    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Набор пресетов для обмена между машинами и людьми.
/// </summary>
/// <remarks>
/// Версия формата указана явно: файл, сохранённый сегодня, должен читаться будущими
/// версиями продукта либо быть отвергнут с внятным объяснением — но не разобран неверно.
/// </remarks>
public sealed record PresetBundle
{
    /// <summary>Текущая версия формата обмена.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Product { get; init; } = "Storm Machine";

    public string? ExportedBy { get; init; }

    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;

    public required List<PortablePreset> Presets { get; init; }
}

/// <summary>Что произошло при импорте.</summary>
public sealed record PresetImportReport
{
    public required int Added { get; init; }

    public required int Updated { get; init; }

    public required int Skipped { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public int Total => Added + Updated + Skipped;
}
