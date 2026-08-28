using StormMachine.Domain.Results;

namespace StormMachine.Domain.Scenarios;

/// <summary>
/// Ставит вердикт результату по заданным порогам.
/// </summary>
/// <remarks>
/// Отдельно от пробы — принцип 4 анализа §8.2. Проба измеряет, но не судит: судить
/// не по чему, пока человек не назвал границу. Отсюда следует, что один и тот же прогон
/// можно переоценить другими порогами, ничего не измеряя заново.
/// </remarks>
public static class ThresholdEvaluator
{
    public static Verdict Evaluate(
        ProbeResult result,
        IReadOnlyList<Threshold> thresholds,
        ProbeResultShape shape = ProbeResultShape.ScalarSeries)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(thresholds);

        // Считается по целому событию, а не по сэмплам: у водопада успешная фаза
        // при оборванной попытке — не ответ, а половина ответа.
        var whole = ProbeMetrics.WholeEvent(result.Samples, shape);

        // Проба, не получившая ни одного годного ответа, не оценивается порогами: у неё
        // нет метрик, и «p95 в норме» на пустом ряду означало бы, что всё хорошо
        // там, где на самом деле не отвечает вообще ничего.
        if (whole.SuccessCount == 0)
        {
            return Verdict.Fail(
                whole.SentCount == 0
                    ? "Ни одной пробы не отправлено."
                    : DescribeSilence(result.Samples, whole.SentCount));
        }

        if (thresholds.Count == 0)
        {
            return Verdict.NotEvaluated("Пороги не заданы — оценивать не по чему.");
        }

        var metrics = ProbeMetrics.Read(result, shape);
        var violations = new List<(Threshold Threshold, double Actual)>();
        var missing = new List<Threshold>();

        foreach (var threshold in thresholds)
        {
            if (!metrics.TryGetValue(threshold.Metric, out var actual))
            {
                missing.Add(threshold);
                continue;
            }

            if (!threshold.IsSatisfiedBy(actual))
            {
                violations.Add((threshold, actual));
            }
        }

        if (violations.Count > 0)
        {
            return Describe(violations, missing);
        }

        // Порог, для которого проба не даёт метрики, — не «прошло». Это молчание,
        // и выдавать его за успех значило бы сообщить, что проверили то, чего
        // не проверяли.
        if (missing.Count == thresholds.Count)
        {
            return Verdict.NotEvaluated(Unmeasured(missing));
        }

        var checkedCount = thresholds.Count - missing.Count;

        var verdict = Verdict.Pass(
            checkedCount == 1
                ? $"Порог соблюдён: {thresholds.First(t => !missing.Contains(t)).Describe()}."
                : $"Все пороги соблюдены ({checkedCount}).");

        return missing.Count == 0
            ? verdict
            : verdict with { Explanation = Unmeasured(missing) };
    }

    private static Verdict Describe(
        List<(Threshold Threshold, double Actual)> violations,
        List<Threshold> missing)
    {
        // Худший из нарушенных задаёт уровень: одно нарушение уровня «отказ»
        // важнее любого числа предупреждений.
        var worst = violations.Max(v => v.Threshold.Level);
        var (threshold, actual) = violations.First(v => v.Threshold.Level == worst);

        var summary = violations.Count == 1
            ? $"{threshold.Metric} = {ProbeMetrics.Format(threshold.Metric, actual)}, "
              + $"порог {threshold.Describe()}."
            : $"Нарушено порогов: {violations.Count}. Худшее — {threshold.Metric} = "
              + $"{ProbeMetrics.Format(threshold.Metric, actual)} при пороге {threshold.Describe()}.";

        var verdict = new Verdict
        {
            Level = worst,
            Summary = summary,
            MetricName = threshold.Metric,
            MetricValue = actual,
            Threshold = threshold.Value,
            Explanation = violations.Count > 1
                ? string.Join(
                    "; ",
                    violations.Select(v => $"{v.Threshold.Describe()} — фактически "
                                           + ProbeMetrics.Format(v.Threshold.Metric, v.Actual)))
                : null,
        };

        return missing.Count == 0
            ? verdict
            : verdict with
            {
                Explanation = string.Join(
                    " ",
                    new[] { verdict.Explanation, Unmeasured(missing) }.Where(s => s is { Length: > 0 })),
            };
    }

    /// <summary>
    /// Чем именно закончились все попытки.
    /// </summary>
    /// <remarks>
    /// «Не получил ответа» и «получил отказ» — разные неисправности. Резолвер,
    /// ответивший NXDOMAIN за четыре миллисекунды, работает безупречно: не существует
    /// имени. Назвать это молчанием значило бы отправить оператора чинить сеть там,
    /// где чинить надо запись.
    /// </remarks>
    private static string DescribeSilence(IReadOnlyList<Measurements.Sample> samples, int sent)
    {
        var refusals = samples
            .Where(s => s.Status == Measurements.SampleStatus.Rejected)
            .Select(s => s.RespondedBy)
            .Where(r => r is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (refusals.Count == 0)
        {
            return $"Ни один из {sent} запросов не получил ответа.";
        }

        return $"Ответ получен на все {sent} запросов, но ни один не годен: "
               + string.Join(", ", refusals) + ".";
    }

    private static string Unmeasured(List<Threshold> missing) =>
        $"Не проверено, потому что проба не даёт таких метрик: "
        + string.Join(", ", missing.Select(t => t.Metric).Distinct(StringComparer.OrdinalIgnoreCase))
        + ".";
}
