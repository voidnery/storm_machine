namespace StormMachine.Domain.Results;

public enum VerdictLevel
{
    /// <summary>Пороги не заданы — оценивать не по чему.</summary>
    Unknown,

    Pass,
    Warn,
    Fail,
}

/// <summary>
/// Оценка результата человеческим языком.
/// </summary>
/// <remarks>
/// Вердикт отделён от измерения (принцип 4, docs/01-analysis.md §8.2): пороги — это
/// конфигурация пресета, а не логика пробы. Один и тот же прогон можно переоценить
/// с другими порогами.
/// <para>
/// Поля <see cref="MetricName"/>, <see cref="MetricValue"/> и <see cref="Threshold"/>
/// существуют ради UX-принципа «объяснимость»: рядом с вердиктом всегда видно,
/// какая метрика и какой порог его дали.
/// </para>
/// </remarks>
public sealed record Verdict
{
    public required VerdictLevel Level { get; init; }

    /// <summary>Короткая формулировка: «Потери 12% — канал непригоден для VoIP».</summary>
    public required string Summary { get; init; }

    public string? MetricName { get; init; }

    public double? MetricValue { get; init; }

    public double? Threshold { get; init; }

    /// <summary>Развёрнутое пояснение, если короткой формулировки мало.</summary>
    public string? Explanation { get; init; }

    /// <summary>Основание вердикта в виде строки — для UI и для отчёта.</summary>
    public string? Reasoning => MetricName is null || MetricValue is null || Threshold is null
        ? null
        : $"{MetricName} = {MetricValue:F3}, порог {Threshold:F3}";

    public static Verdict NotEvaluated(string reason = "Пороги не заданы") => new()
    {
        Level = VerdictLevel.Unknown,
        Summary = reason,
    };

    public static Verdict Pass(string summary, string? metric = null, double? value = null, double? threshold = null) =>
        new() { Level = VerdictLevel.Pass, Summary = summary, MetricName = metric, MetricValue = value, Threshold = threshold };

    public static Verdict Warn(string summary, string? metric = null, double? value = null, double? threshold = null) =>
        new() { Level = VerdictLevel.Warn, Summary = summary, MetricName = metric, MetricValue = value, Threshold = threshold };

    public static Verdict Fail(string summary, string? metric = null, double? value = null, double? threshold = null) =>
        new() { Level = VerdictLevel.Fail, Summary = summary, MetricName = metric, MetricValue = value, Threshold = threshold };
}
