using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.Scenarios;

/// <summary>
/// Числа результата, по которым можно ставить пороги.
/// </summary>
/// <remarks>
/// Новых измерений здесь нет — только чтение того, что уже собрано двумя каналами:
/// агрегатами по сэмплам и числовыми фактами. Это и есть причина, по которой канал
/// фактов в И-2 сделали отдельным: без него срок до истечения сертификата и код ответа
/// пришлось бы доставать разбором текста.
/// <para>
/// Ключи агрегатов латинские и короткие — их набирают в порогах. Имена фактов остаются
/// как есть: они и так на русском, и переводить их значило бы завести второй словарь,
/// который рано или поздно разойдётся с первым.
/// </para>
/// <para>
/// Чтение зависит от формы результата, и это не деталь. Первая версия считала перцентили
/// по всем сэмплам подряд, и на HTTP это давало число, которому не соответствовало
/// ничего происходившего: один запрос даёт пять длительностей — разрешение имени,
/// соединение, рукопожатие, первый байт, скачивание, — и «p95 по всем пяти» смешивает
/// 4 мс разрешения имени с 300 мс скачивания в одно распределение. Для водопада целым
/// событием является сумма фаз в пределах попытки, а не выборка из фаз.
/// </para>
/// </remarks>
public static class ProbeMetrics
{
    /// <summary>Отделяет имя ряда от имени метрики: <c>p95@ttfb</c>, <c>loss@1.1.1.1</c>.</summary>
    public const char SeriesSeparator = '@';

    /// <summary>Метрики, доступные у любой пробы со скалярным рядом.</summary>
    public static IReadOnlyList<string> Common { get; } =
        ["min", "p50", "p95", "p99", "max", "mean", "jitter", "pdv", "loss", "sent", "received"];

    public static IReadOnlyDictionary<string, double> Read(ProbeResult result, ProbeResultShape shape)
    {
        ArgumentNullException.ThrowIfNull(result);

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        Add(metrics, null, WholeEvent(result.Samples, shape));

        // Ряды внутри результата доступны порогам по отдельности: «p95@ttfb < 500»
        // спрашивает про сервер, а не про канал, и это разные вопросы. Для traceroute
        // рядов не заводим — там своя разбивка по хопам с собственным разбором.
        if (shape is ProbeResultShape.PhasedTiming or ProbeResultShape.ComparedSeries)
        {
            foreach (var series in SeriesBreakdown.Compute(shape, result.Samples))
            {
                Add(metrics, series.Key, series);
            }
        }

        // MOS — только для скалярного ряда. Это оценка разговорной связи по задержке,
        // дрожанию и потерям одного потока; считать её по сумме фаз HTTP или по смеси
        // пяти резолверов значило бы выдать число, которое не про что.
        if (shape == ProbeResultShape.ScalarSeries)
        {
            var statistics = LatencyStatistics.Compute(result.Samples);

            if (statistics.SampleCount > 0)
            {
                var voice = VoiceQualityEstimate.Estimate(statistics, result.LossPercent);

                if (!double.IsNaN(voice.Mos))
                {
                    metrics["mos"] = voice.Mos;
                }
            }
        }

        foreach (var fact in result.Facts)
        {
            if (fact.Numeric is { } value)
            {
                metrics[fact.Name] = value;
            }
        }

        return metrics;
    }

    /// <summary>
    /// Результат целиком как один ряд.
    /// </summary>
    /// <remarks>
    /// Для водопада попытка сворачивается в сумму своих фаз, и тогда <c>sent</c> считает
    /// запросы, а не длительности: порог «loss &lt; 1» на шаге «Страница» спрашивает,
    /// сколько запросов не прошло, а не сколько фаз не измерилось.
    /// </remarks>
    public static SeriesStatistics WholeEvent(IReadOnlyList<Sample> samples, ProbeResultShape shape)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return SeriesBreakdown.WholeRun(
            shape == ProbeResultShape.PhasedTiming ? Totals(samples) : samples);
    }

    /// <summary>
    /// Сумма фаз в пределах попытки.
    /// </summary>
    /// <remarks>
    /// Попытка считается неудачной целиком, если не удалась хотя бы одна её фаза:
    /// соединение, оборвавшееся на рукопожатии, не является наполовину успешным.
    /// </remarks>
    private static List<Sample> Totals(IReadOnlyList<Sample> samples)
    {
        var order = new List<int>();
        var totals = new Dictionary<int, Sample>();

        foreach (var sample in samples)
        {
            var key = sample.Group ?? sample.Sequence;

            if (!totals.TryGetValue(key, out var running))
            {
                order.Add(key);

                totals[key] = sample.IsSuccess
                    ? new Sample
                    {
                        Sequence = key,
                        TimestampUtc = sample.TimestampUtc,
                        Value = sample.Value,
                        Status = SampleStatus.Success,
                    }
                    : Sample.Failed(key, sample.TimestampUtc, sample.Status);

                continue;
            }

            if (!running.IsSuccess)
            {
                continue;
            }

            totals[key] = sample.IsSuccess
                ? running with { Value = running.Value + sample.Value }
                : Sample.Failed(key, running.TimestampUtc, sample.Status);
        }

        var result = new List<Sample>(order.Count);

        foreach (var key in order)
        {
            result.Add(totals[key]);
        }

        return result;
    }

    private static void Add(Dictionary<string, double> metrics, string? series, SeriesStatistics values)
    {
        var suffix = string.IsNullOrEmpty(series) ? string.Empty : SeriesSeparator + series;

        metrics["sent" + suffix] = values.SentCount;
        metrics["received" + suffix] = values.SuccessCount;
        metrics["loss" + suffix] = values.LossPercent;

        var statistics = values.Statistics;

        if (statistics.SampleCount == 0)
        {
            return;
        }

        metrics["min" + suffix] = statistics.MinMs;
        metrics["p50" + suffix] = statistics.P50Ms;
        metrics["p95" + suffix] = statistics.P95Ms;
        metrics["p99" + suffix] = statistics.P99Ms;
        metrics["max" + suffix] = statistics.MaxMs;
        metrics["mean" + suffix] = statistics.MeanMs;
        metrics["jitter" + suffix] = statistics.JitterRfc3550Ms;
        metrics["pdv" + suffix] = statistics.PdvMs;
    }

    /// <summary>
    /// Метрики из сохранённого прогона — по агрегатам, а не по сырым измерениям.
    /// </summary>
    /// <remarks>
    /// Нужна там, где сырых сэмплов уже нет: политика хранения удаляет их раньше,
    /// чем сами прогоны. Эталон, снятый с прошлогоднего измерения, обязан считаться
    /// и тогда — иначе он бы устаревал ровно к тому моменту, когда становится нужен.
    /// <para>
    /// Что здесь недоступно, сказано прямо: MOS не считается. Он требует ряда
    /// задержек, а не их сводки, и подставить его из агрегатов нельзя.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, double> FromStored(
        IReadOnlyList<SeriesStatistics> series,
        IReadOnlyList<ProbeFact> facts)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(facts);

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in series)
        {
            Add(metrics, item.Key == SeriesBreakdown.WholeRunKey ? null : item.Key, item);
        }

        foreach (var fact in facts)
        {
            if (fact.Numeric is { } value)
            {
                metrics[fact.Name] = value;
            }
        }

        return metrics;
    }

    /// <summary>Что показать оператору как список доступных метрик.</summary>
    public static IReadOnlyList<string> Available(ProbeResult result, ProbeResultShape shape) =>
        [.. Read(result, shape).Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Имя метрики без ряда: <c>p95@ttfb</c> → <c>p95</c>.</summary>
    public static string BaseOf(string metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var at = metric.IndexOf(SeriesSeparator, StringComparison.Ordinal);

        return at < 0 ? metric : metric[..at];
    }

    /// <summary>Единица измерения метрики — для показа рядом со значением.</summary>
    public static string UnitOf(string metric) => BaseOf(metric).ToLowerInvariant() switch
    {
        "loss" => "%",
        "sent" or "received" => string.Empty,
        "mos" => string.Empty,
        "min" or "p50" or "p95" or "p99" or "max" or "mean" or "jitter" or "pdv" => "мс",
        _ => string.Empty,
    };

    public static string Format(string metric, double value)
    {
        var unit = UnitOf(metric);
        var text = value.ToString(FormatOf(BaseOf(metric), value), CultureInfo.InvariantCulture);

        return unit.Length == 0 ? text : $"{text} {unit}";
    }

    /// <summary>
    /// Сколько знаков показывать.
    /// </summary>
    /// <remarks>
    /// «244.16 мс» сообщает точность, которой у измерения нет: собственный порог часов
    /// — доли миллисекунды, но сетевое измерение колеблется на единицы. Доли остаются
    /// там, где они различимы: у значений меньше десяти миллисекунд и у MOS, вся шкала
    /// которого укладывается в четыре единицы.
    /// </remarks>
    private static string FormatOf(string name, double value) => name.ToLowerInvariant() switch
    {
        "sent" or "received" => "0",
        "mos" => "0.00",
        "loss" => "0.#",
        "min" or "p50" or "p95" or "p99" or "max" or "mean" or "jitter" or "pdv" =>
            Math.Abs(value) < 10 ? "0.0" : "0",
        _ => "0.###",
    };
}
