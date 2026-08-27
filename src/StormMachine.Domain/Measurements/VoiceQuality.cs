namespace StormMachine.Domain.Measurements;

/// <summary>Оценка пригодности канала для голоса.</summary>
public sealed record VoiceQuality
{
    /// <summary>R-фактор по E-модели: 0…93.2 для узкополосного кодека.</summary>
    public required double RFactor { get; init; }

    /// <summary>Средняя субъективная оценка, 1.0…4.5.</summary>
    public required double Mos { get; init; }

    /// <summary>Словесная оценка для человека.</summary>
    public string Grade => Mos switch
    {
        >= 4.3 => "отличное",
        >= 4.0 => "хорошее",
        >= 3.6 => "приемлемое",
        >= 3.1 => "плохое",
        _ => "непригодное",
    };

    /// <summary>Годится ли канал для телефонии.</summary>
    public bool IsAcceptableForVoice => Mos >= 3.6;

    public static readonly VoiceQuality Unknown = new() { RFactor = double.NaN, Mos = double.NaN };
}

/// <summary>
/// Оценка качества голосовой связи по задержке, джиттеру и потерям.
/// </summary>
/// <remarks>
/// Реализована <b>упрощённая</b> E-модель ITU-T G.107 — та же, что используют сетевые
/// инструменты и оборудование. Полная модель учитывает кодек, размер буфера дрожания,
/// эхо и десяток других коэффициентов; здесь они приняты равными значениям по умолчанию
/// для узкополосного канала.
/// <para>
/// Это принципиальная оговорка, а не мелочь: результат отвечает на вопрос «выдержит ли
/// сеть телефонию», но не заменяет измерение качества конкретного кодека. Отчёт обязан
/// называть модель упрощённой, иначе цифра будет выглядеть точнее, чем есть.
/// </para>
/// </remarks>
public static class VoiceQualityEstimate
{
    /// <summary>Базовый R-фактор идеального узкополосного канала (G.107).</summary>
    private const double BaseRFactor = 93.2;

    /// <summary>
    /// Вклад буфера дрожания: приёмник компенсирует джиттер, накапливая задержку.
    /// Коэффициент 2 — обычное допущение для буфера, покрывающего два стандартных отклонения.
    /// </summary>
    private const double JitterBufferFactor = 2.0;

    /// <summary>Постоянная задержка обработки: кодирование, пакетизация, воспроизведение.</summary>
    private const double ProcessingDelayMs = 10.0;

    /// <summary>
    /// Штраф за каждый процент потерь.
    /// </summary>
    /// <remarks>
    /// Голос переносит потери гораздо хуже, чем передача файлов: пропавший пакет —
    /// это провал в речи, а не повод для повторной отправки.
    /// </remarks>
    private const double LossPenaltyPerPercent = 2.5;

    public static VoiceQuality Estimate(double latencyMs, double jitterMs, double lossPercent)
    {
        if (double.IsNaN(latencyMs) || latencyMs < 0)
        {
            return VoiceQuality.Unknown;
        }

        var jitter = double.IsNaN(jitterMs) ? 0 : Math.Max(0, jitterMs);
        var loss = double.IsNaN(lossPercent) ? 0 : Math.Clamp(lossPercent, 0, 100);

        var effectiveLatency = latencyMs + (jitter * JitterBufferFactor) + ProcessingDelayMs;

        // Перелом на 160 мс — из G.114: до него задержка почти не мешает разговору,
        // после начинает ломать очерёдность реплик.
        var rFactor = effectiveLatency < 160
            ? BaseRFactor - (effectiveLatency / 40.0)
            : BaseRFactor - ((effectiveLatency - 120) / 10.0);

        rFactor -= loss * LossPenaltyPerPercent;
        rFactor = Math.Clamp(rFactor, 0, BaseRFactor);

        var mos = rFactor < 0
            ? 1.0
            : 1.0 + (0.035 * rFactor) + (0.000007 * rFactor * (rFactor - 60) * (100 - rFactor));

        return new VoiceQuality
        {
            RFactor = rFactor,
            Mos = Math.Clamp(mos, 1.0, 4.5),
        };
    }

    /// <summary>Оценка по готовым агрегатам ряда.</summary>
    public static VoiceQuality Estimate(LatencyStatistics statistics, double lossPercent)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        return statistics.SampleCount == 0
            ? VoiceQuality.Unknown
            : Estimate(statistics.MeanMs, statistics.JitterRfc3550Ms, lossPercent);
    }
}
