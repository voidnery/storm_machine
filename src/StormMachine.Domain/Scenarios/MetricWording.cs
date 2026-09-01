namespace StormMachine.Domain.Scenarios;

/// <summary>Метрика порога: ключ, как её называют, в чём измеряется и что означает.</summary>
public sealed record MetricHelp(string Key, string Title, string Unit, string About);

/// <summary>
/// Как продукт называет метрики, по которым ставят пороги.
/// </summary>
/// <remarks>
/// Ключи порогов латинские и короткие — их набирают руками, и переводить их значило бы
/// сломать уже написанные пороги и сценарии. Но короткий ключ ничего не объясняет:
/// поле «Пороги» с подсказкой <c>p95 &lt; 50</c> не говорит ни что такое p95, ни в чём
/// эти 50 (замечание оператора). Словарь отвечает на оба вопроса и живёт рядом
/// с <see cref="ProbeMetrics"/>, который эти ключи и производит: разойтись им нельзя.
/// <para>
/// Единица берётся у <see cref="ProbeMetrics.UnitOf"/> — единственного места, где она
/// объявлена, а не переписывается здесь ещё раз.
/// </para>
/// </remarks>
public static class MetricWording
{
    private static readonly Dictionary<string, (string Title, string About)> Words =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["min"] = ("минимум", "самый быстрый ответ за прогон"),
            ["p50"] = ("медиана", "половина ответов быстрее этого значения, половина медленнее"),
            ["p95"] = ("95-й перцентиль", "быстрее этого значения 95 ответов из ста; по нему судят о худших случаях"),
            ["p99"] = ("99-й перцентиль", "быстрее этого значения 99 ответов из ста; редкие выбросы"),
            ["max"] = ("максимум", "самый долгий ответ за прогон"),
            ["mean"] = ("среднее", "среднее арифметическое; один выброс тянет его за собой, поэтому судят обычно по медиане"),
            ["jitter"] = ("джиттер", "дрожание задержки по RFC 3550: насколько неровно приходят ответы"),
            ["pdv"] = ("разброс задержки", "p99 минус медиана: насколько худшие случаи хуже обычных"),
            ["loss"] = ("потери", "доля проб, оставшихся без ответа"),
            ["sent"] = ("отправлено", "сколько проб отправлено"),
            ["received"] = ("получено", "сколько проб получили ответ"),
            ["mos"] = ("оценка речи MOS", "качество разговорной связи по шкале от 1 до 5; считается по задержке, дрожанию и потерям"),
        };

    /// <summary>Как называется метрика. Незнакомый ключ возвращается как есть.</summary>
    public static string Title(string metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return Words.TryGetValue(ProbeMetrics.BaseOf(metric), out var word) ? word.Title : metric;
    }

    /// <summary>Что метрика означает. Пусто, если это имя факта пробы, а не агрегат.</summary>
    public static string About(string metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return Words.TryGetValue(ProbeMetrics.BaseOf(metric), out var word) ? word.About : string.Empty;
    }

    /// <summary>Метрика и её единица одной строкой: «медиана, мс».</summary>
    public static string TitleWithUnit(string metric)
    {
        var unit = ProbeMetrics.UnitOf(metric);

        return unit.Length == 0 ? Title(metric) : $"{Title(metric)}, {unit}";
    }

    /// <summary>
    /// Порог человеческими словами: «p95 &lt; 50» → «95-й перцентиль меньше 50 мс».
    /// </summary>
    /// <remarks>
    /// Показывается рядом с набранным порогом, а не вместо него: набирают короткую
    /// запись, читают длинную.
    /// </remarks>
    public static string Explain(Threshold threshold)
    {
        ArgumentNullException.ThrowIfNull(threshold);

        var unit = ProbeMetrics.UnitOf(threshold.Metric);
        var value = threshold.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var sign = threshold.Comparison switch
        {
            Comparison.LessThan => "меньше",
            Comparison.AtMost => "не больше",
            Comparison.GreaterThan => "больше",
            _ => "не меньше",
        };

        return unit.Length == 0
            ? $"{Title(threshold.Metric)} {sign} {value}"
            : $"{Title(threshold.Metric)} {sign} {value} {unit}";
    }

    /// <summary>Метрики, доступные любой пробе со скалярным рядом, — для подсказки в форме.</summary>
    public static IReadOnlyList<MetricHelp> Common { get; } =
    [
        .. ProbeMetrics.Common.Select(key => new MetricHelp(
            key,
            Title(key),
            ProbeMetrics.UnitOf(key),
            About(key))),
    ];
}
