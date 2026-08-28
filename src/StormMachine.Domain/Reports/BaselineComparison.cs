using System.Globalization;
using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.Reports;

/// <summary>Куда сдвинулось значение.</summary>
public enum ChangeDirection
{
    /// <summary>Сдвиг есть, но он в пределах шума. Изменением не считается.</summary>
    Same,

    Better,

    Worse,
}

/// <summary>Изменение одной метрики относительно эталона.</summary>
public sealed record MetricChange
{
    public required string Name { get; init; }

    public required double Before { get; init; }

    public required double After { get; init; }

    public required ChangeDirection Direction { get; init; }

    /// <summary>Почему сдвиг признан незначимым, если признан.</summary>
    public string? Insignificance { get; init; }

    public double Delta => After - Before;

    public double? Percent => Before == 0 ? null : (After - Before) / Math.Abs(Before) * 100;

    public string Describe(MeasurementUnit unit)
    {
        var suffix = unit switch
        {
            MeasurementUnit.Milliseconds => " мс",
            MeasurementUnit.MegabitsPerSecond => " Мбит/с",
            MeasurementUnit.Percent => " %",
            _ => string.Empty,
        };

        var before = Before.ToString("0.###", CultureInfo.InvariantCulture);
        var after = After.ToString("0.###", CultureInfo.InvariantCulture);
        var change = Percent is { } percent
            ? $" ({(percent >= 0 ? "+" : string.Empty)}{percent.ToString("0.#", CultureInfo.InvariantCulture)} %)"
            : string.Empty;

        return $"{before}{suffix} → {after}{suffix}{change}";
    }
}

/// <summary>Расхождение условий между эталоном и текущим измерением.</summary>
/// <param name="What">Что именно разошлось.</param>
/// <param name="Before">Как было при фиксации эталона.</param>
/// <param name="After">Как стало.</param>
/// <param name="IsSevere">
/// Расхождение, при котором сравнивать числа напрямую нельзя.
/// </param>
public sealed record ConditionMismatch(string What, string Before, string After, bool IsSevere);

/// <summary>
/// Сравнение измерения с эталоном.
/// </summary>
/// <remarks>
/// Отвечает на вопрос «стало лучше или хуже» — и вместе с ответом всегда несёт то,
/// что делает ответ осмысленным или бессмысленным: расхождения условий. Число «стало
/// на 40 % быстрее» без пометки «эталон снят по Wi-Fi, сейчас кабель» — не вывод,
/// а красивая ошибка.
/// </remarks>
public sealed record BaselineComparison
{
    public required Baseline Baseline { get; init; }

    public required MeasurementContext Context { get; init; }

    public required DateTimeOffset ComparedUtc { get; init; }

    public required IReadOnlyList<MetricChange> Changes { get; init; }

    public required IReadOnlyList<ConditionMismatch> Mismatches { get; init; }

    /// <summary>Метрики эталона, которых в текущем измерении не оказалось.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    public int BetterCount => Changes.Count(c => c.Direction == ChangeDirection.Better);

    public int WorseCount => Changes.Count(c => c.Direction == ChangeDirection.Worse);

    public bool HasSevereMismatch => Mismatches.Any(m => m.IsSevere);

    /// <summary>
    /// Итог одной строкой.
    /// </summary>
    /// <remarks>
    /// «Смешанно» — полноценный ответ, а не отговорка: задержка может упасть,
    /// а потери вырасти, и свести это к одному слову значило бы выбрать за читателя,
    /// что для него важнее.
    /// </remarks>
    public string Verdict => (BetterCount, WorseCount) switch
    {
        (0, 0) => "без изменений",
        (> 0, 0) => "стало лучше",
        (0, > 0) => "стало хуже",
        _ => "смешанно: часть метрик лучше, часть хуже",
    };
}

/// <summary>Сравнение с эталоном.</summary>
public static class BaselineComparer
{
    /// <summary>
    /// Относительный порог значимости.
    /// </summary>
    /// <remarks>
    /// Пять процентов. Сеть не воспроизводит саму себя точнее: два одинаковых прогона
    /// подряд расходятся на единицы процентов просто так. Продукт, объявляющий
    /// ухудшением каждый такой сдвиг, за неделю обесценивает собственное слово.
    /// </remarks>
    public const double SignificantPercent = 5;

    /// <summary>
    /// Во сколько раз калибровочный базис должен отличаться, чтобы это назвать.
    /// </summary>
    /// <remarks>
    /// Базис — накладные расходы измерительного стека. Он гуляет от загрузки машины,
    /// и мелкий разброс ничего не значит. Двукратный — значит: измеряли на разных
    /// по загрузке машинах, и мелкие величины сравнивать нельзя.
    /// </remarks>
    private const double CalibrationFactor = 2;

    public static BaselineComparison Compare(
        Baseline baseline,
        IReadOnlyDictionary<string, double> metrics,
        MeasurementContext context,
        DateTimeOffset? comparedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(context);

        var changes = new List<MetricChange>();
        var missing = new List<string>();

        foreach (var metric in baseline.Metrics)
        {
            if (!metrics.TryGetValue(metric.Name, out var actual))
            {
                missing.Add(metric.Name);

                continue;
            }

            changes.Add(Evaluate(metric, actual, baseline, context));
        }

        return new BaselineComparison
        {
            Baseline = baseline,
            Context = context,
            ComparedUtc = comparedUtc ?? DateTimeOffset.UtcNow,
            Changes = changes,
            Mismatches = Mismatches(baseline.Context, context),
            Missing = missing,
        };
    }

    private static MetricChange Evaluate(
        BaselineMetric metric,
        double actual,
        Baseline baseline,
        MeasurementContext context)
    {
        var delta = actual - metric.Value;

        if (delta == 0)
        {
            return new MetricChange
            {
                Name = metric.Name,
                Before = metric.Value,
                After = actual,
                Direction = ChangeDirection.Same,
            };
        }

        // Порог шума измерения: для времени — калибровочный базис, ниже которого
        // продукт вообще не берётся различать величины. Сравнивать под ним значило бы
        // выдавать за изменение собственные накладные расходы.
        var floor = baseline.Unit == MeasurementUnit.Milliseconds
            ? Math.Max(baseline.Context.CalibrationBaselineMs, context.CalibrationBaselineMs)
            : 0;

        if (Math.Abs(delta) <= floor)
        {
            return new MetricChange
            {
                Name = metric.Name,
                Before = metric.Value,
                After = actual,
                Direction = ChangeDirection.Same,
                // Сам сдвиг не повторяется словами: столбцы «эталон» и «сейчас» уже
                // показывают его, причём округлёнными. Число, посчитанное по полной
                // точности, отличалось бы от их разности на последнем знаке — и читатель,
                // сложивший одно с другим, вправе не поверить обоим.
                Insignificance =
                    $"ниже порога достоверности {floor.ToString("0.###", CultureInfo.InvariantCulture)} мс — "
                    + "различить нельзя",
            };
        }

        var percent = metric.Value == 0 ? double.PositiveInfinity : Math.Abs(delta) / Math.Abs(metric.Value) * 100;

        if (percent < SignificantPercent)
        {
            return new MetricChange
            {
                Name = metric.Name,
                Before = metric.Value,
                After = actual,
                Direction = ChangeDirection.Same,
                Insignificance =
                    $"меньше {SignificantPercent.ToString("0", CultureInfo.InvariantCulture)} % — "
                    + "столько сеть расходится сама с собой",
            };
        }

        var improved = metric.HigherIsBetter ? delta > 0 : delta < 0;

        return new MetricChange
        {
            Name = metric.Name,
            Before = metric.Value,
            After = actual,
            Direction = improved ? ChangeDirection.Better : ChangeDirection.Worse,
        };
    }

    /// <summary>
    /// Чем условия текущего измерения отличаются от условий эталона.
    /// </summary>
    /// <remarks>
    /// Тяжёлыми считаются расхождения, при которых числа несопоставимы в принципе:
    /// смена типа адаптера и смена внешней службы. Остальные — повод посмотреть
    /// внимательнее, а не отказаться от сравнения.
    /// </remarks>
    private static List<ConditionMismatch> Mismatches(MeasurementContext before, MeasurementContext after)
    {
        var found = new List<ConditionMismatch>();

        // Смена места — тяжёлое расхождение, и по той же причине, что смена адаптера:
        // канал до филиала и канал до шлюза в офисе — разные каналы, а не один канал
        // в разном состоянии. Сравнивать их числа напрямую нельзя.
        if (!string.Equals(before.Profile, after.Profile, StringComparison.OrdinalIgnoreCase))
        {
            found.Add(new ConditionMismatch(
                "профиль окружения",
                before.Profile ?? "не выбран",
                after.Profile ?? "не выбран",
                IsSevere: before.Profile is not null && after.Profile is not null));
        }

        if (before.AdapterKind != after.AdapterKind)
        {
            found.Add(new ConditionMismatch(
                "тип адаптера",
                Describe(before.AdapterKind),
                Describe(after.AdapterKind),
                IsSevere: true));
        }

        if (!string.Equals(before.Backend, after.Backend, StringComparison.OrdinalIgnoreCase))
        {
            found.Add(new ConditionMismatch(
                "внешняя служба",
                before.Backend ?? "не использовалась",
                after.Backend ?? "не использовалась",
                IsSevere: true));
        }

        if (!string.Equals(before.InterfaceName, after.InterfaceName, StringComparison.OrdinalIgnoreCase))
        {
            found.Add(new ConditionMismatch(
                "интерфейс",
                before.InterfaceName,
                after.InterfaceName,
                IsSevere: false));
        }

        if (before.CalibrationBaselineMs > 0
            && after.CalibrationBaselineMs > 0
            && (after.CalibrationBaselineMs > before.CalibrationBaselineMs * CalibrationFactor
                || before.CalibrationBaselineMs > after.CalibrationBaselineMs * CalibrationFactor))
        {
            found.Add(new ConditionMismatch(
                "порог достоверности",
                $"{before.CalibrationBaselineMs.ToString("0.###", CultureInfo.InvariantCulture)} мс",
                $"{after.CalibrationBaselineMs.ToString("0.###", CultureInfo.InvariantCulture)} мс",
                IsSevere: false));
        }

        if (!string.Equals(before.ProductVersion, after.ProductVersion, StringComparison.Ordinal))
        {
            found.Add(new ConditionMismatch(
                "версия продукта",
                before.ProductVersion,
                after.ProductVersion,
                IsSevere: false));
        }

        return found;
    }

    private static string Describe(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "не определён",
    };
}
