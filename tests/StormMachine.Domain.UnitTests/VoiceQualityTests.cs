using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки оценки качества голосовой связи.
/// </summary>
/// <remarks>
/// Модель упрощённая, и точные значения R-фактора закреплять смысла нет — они зависят
/// от коэффициентов, которые в полной модели настраиваются под кодек. Закрепляется другое:
/// <b>порядок</b>. Худшая сеть обязана давать худшую оценку, потери обязаны бить сильнее
/// задержки, а границы словесных оценок — совпадать с общепринятыми.
/// </remarks>
public sealed class VoiceQualityTests
{
    [Fact]
    public void PerfectChannel_ScoresNearMaximum()
    {
        var quality = VoiceQualityEstimate.Estimate(latencyMs: 5, jitterMs: 0.5, lossPercent: 0);

        Assert.True(quality.Mos > 4.3, $"MOS {quality.Mos:0.00} — идеальный канал должен получать высшую оценку.");
        Assert.Equal("отличное", quality.Grade);
        Assert.True(quality.IsAcceptableForVoice);
    }

    [Fact]
    public void Loss_DegradesQualityFasterThanLatency()
    {
        // Пятипроцентные потери должны быть хуже полусекундной задержки: пропавший
        // пакет — это провал в речи, а задержка всего лишь ломает очерёдность реплик.
        var lossy = VoiceQualityEstimate.Estimate(latencyMs: 20, jitterMs: 1, lossPercent: 5);
        var slow = VoiceQualityEstimate.Estimate(latencyMs: 200, jitterMs: 1, lossPercent: 0);

        Assert.True(lossy.Mos < slow.Mos, $"потери {lossy.Mos:0.00} должны быть хуже задержки {slow.Mos:0.00}.");
    }

    [Fact]
    public void Jitter_CountsThroughBuffer()
    {
        var steady = VoiceQualityEstimate.Estimate(latencyMs: 50, jitterMs: 0, lossPercent: 0);
        var jumpy = VoiceQualityEstimate.Estimate(latencyMs: 50, jitterMs: 40, lossPercent: 0);

        Assert.True(jumpy.Mos < steady.Mos, "Дрожание должно учитываться: приёмник компенсирует его задержкой.");
    }

    [Fact]
    public void HeavyLoss_MakesChannelUnusable()
    {
        var quality = VoiceQualityEstimate.Estimate(latencyMs: 30, jitterMs: 5, lossPercent: 30);

        Assert.False(quality.IsAcceptableForVoice);
        Assert.Equal("непригодное", quality.Grade);
        Assert.True(quality.Mos >= 1.0, "MOS не может опускаться ниже единицы.");
    }

    [Fact]
    public void MonotonicInLatency()
    {
        var previous = double.MaxValue;

        foreach (var latency in new double[] { 10, 50, 100, 150, 200, 300, 500 })
        {
            var mos = VoiceQualityEstimate.Estimate(latency, jitterMs: 1, lossPercent: 0).Mos;

            Assert.True(mos <= previous, $"Задержка {latency} мс дала оценку лучше предыдущей: {mos:0.00} > {previous:0.00}.");
            previous = mos;
        }
    }

    [Fact]
    public void EmptyStatistics_YieldUnknown()
    {
        var quality = VoiceQualityEstimate.Estimate(LatencyStatistics.Compute([]), lossPercent: 0);

        Assert.True(double.IsNaN(quality.Mos), "Без успешных проб оценивать нечего — должно быть «неизвестно».");
    }

    [Fact]
    public void NegativeLatency_YieldsUnknown()
    {
        // Отрицательная задержка означает сбой измерения, а не мгновенный ответ.
        var quality = VoiceQualityEstimate.Estimate(latencyMs: -1, jitterMs: 0, lossPercent: 0);

        Assert.True(double.IsNaN(quality.Mos));
    }

    [Fact]
    public void GradeBoundaries_MatchAcceptanceThreshold()
    {
        // Граница пригодности и словесная оценка обязаны совпадать: иначе отчёт скажет
        // «приемлемое» там, где сам же считает канал непригодным.
        var acceptable = new VoiceQuality { RFactor = 70, Mos = 3.6 };
        var poor = new VoiceQuality { RFactor = 60, Mos = 3.59 };

        Assert.True(acceptable.IsAcceptableForVoice);
        Assert.Equal("приемлемое", acceptable.Grade);
        Assert.False(poor.IsAcceptableForVoice);
        Assert.Equal("плохое", poor.Grade);
    }
}
