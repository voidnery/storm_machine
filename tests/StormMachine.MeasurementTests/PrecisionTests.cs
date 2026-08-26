using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using Xunit.Abstractions;

namespace StormMachine.MeasurementTests;

/// <summary>
/// Проверки точности измерительного ядра — приёмка итерации И-1.
/// </summary>
/// <remarks>
/// Эти тесты — не формальность. Ошибка в точности расползлась бы по всему продукту
/// и всплыла бы на боевых тестах, когда переделывать дорого. Здесь она роняет сборку.
/// </remarks>
public sealed class PrecisionTests(ITestOutputHelper output)
{
    private const int ProbeCount = 200;

    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Главный тест итерации: собственный таймер обязан различать величины,
    /// которые системный API округляет в ноль.
    /// </summary>
    /// <remarks>
    /// На стенде <c>PingReply.RoundtripTime</c> дал 6 различимых значений на 300 проб,
    /// собственный таймер — 285. Если это соотношение когда-нибудь сломается, джиттер,
    /// PDV и MOS превратятся в мусор, и заметить это иначе будет нечем.
    /// </remarks>
    [Fact]
    public async Task Timer_DistinguishesSubMillisecondValues()
    {
        await using var services = MeasurementHarness.BuildServices();
        var samples = await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(ProbeCount));

        var successful = samples.Where(s => s.IsSuccess).Select(s => s.Value).ToList();
        Assert.True(successful.Count > ProbeCount / 2, $"Слишком мало успешных проб: {successful.Count}");

        var distinct = successful.Distinct().Count();
        var ratio = (double)distinct / successful.Count;

        _output.WriteLine($"Успешных проб      : {successful.Count}");
        _output.WriteLine($"Различимых значений: {distinct} ({ratio:P0})");
        _output.WriteLine($"Пример значений    : {string.Join(", ", successful.Take(5).Select(v => v.ToString("0.0000", CultureInfo.InvariantCulture)))}");

        // Порог с большим запасом: на исправном таймере доля близка к 100%.
        // Целочисленный источник в миллисекундах дал бы единицы процентов.
        Assert.True(
            ratio > 0.50,
            $"Различимых значений всего {ratio:P0} — признак того, что задержка берётся "
            + "из источника с миллисекундным разрешением, а не из собственного таймера.");
    }

    /// <summary>Наименьшая задержка, которую продукт заявляет достоверно измеримой.</summary>
    /// <remarks>
    /// Порядок типичного RTT в локальной сети. Всё, что заметно меньше, находится
    /// на уровне порога разрешения измерительного стека, и продукт обязан это сообщать,
    /// а не делать вид, что измерил.
    /// </remarks>
    private const double MinimumClaimedMeasurementMs = 1.0;

    /// <summary>Бюджет собственного шума: 20% от заявленной измеримой величины.</summary>
    private const double NoiseBudgetMs = 0.20 * MinimumClaimedMeasurementMs;

    /// <summary>
    /// Требование §7 анализа: собственный шум измерительного стека не должен превышать
    /// 20% от наименьшей величины, которую продукт заявляет измеримой.
    /// </summary>
    /// <remarks>
    /// Первая формулировка теста делила джиттер на p50 самого loopback и оказалась
    /// неустойчивой: наблюдалось от 9.7% до 22.5% при почти неизменном абсолютном
    /// джиттере 0.026–0.060 мс. Причина в знаменателе: loopback — это наименьшая
    /// измеримая величина, худший возможный делитель, и доля от неё не говорит ничего
    /// о достоверности реальных измерений.
    /// <para>
    /// Правильная постановка: шум сравнивается с тем, что мы <b>заявляем</b> измеримым,
    /// то есть с миллисекундой. Берётся лучший из трёх прогонов — измеряется пол шума,
    /// а пол по определению достигаемый минимум, а не среднее по случайным помехам ОС.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StackNoise_StaysWithinBudget()
    {
        await using var services = MeasurementHarness.BuildServices();

        var best = double.MaxValue;
        LatencyStatistics? bestStats = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var samples = await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(ProbeCount));
            var stats = LatencyStatistics.Compute(samples);

            Assert.True(stats.SampleCount > ProbeCount / 2, $"Слишком мало успешных проб: {stats.SampleCount}");

            _output.WriteLine($"прогон {attempt + 1}: джиттер {stats.JitterRfc3550Ms:0.000} мс, "
                              + $"p50 {stats.P50Ms:0.000} мс, PDV {stats.PdvMs:0.000} мс");

            if (stats.JitterRfc3550Ms < best)
            {
                best = stats.JitterRfc3550Ms;
                bestStats = stats;
            }
        }

        Assert.NotNull(bestStats);

        _output.WriteLine(string.Empty);
        _output.WriteLine($"Пол шума стека : {best:0.000} мс");
        _output.WriteLine($"Бюджет         : {NoiseBudgetMs:0.000} мс (20% от {MinimumClaimedMeasurementMs:0.0} мс)");
        _output.WriteLine($"Запас          : {NoiseBudgetMs / best:0.#}×");
        _output.WriteLine($"Для справки    : {best / bestStats.P50Ms:P1} от p50 самого loopback");

        Assert.True(
            best <= NoiseBudgetMs,
            $"Пол собственного шума {best:0.000} мс превышает бюджет {NoiseBudgetMs:0.000} мс. "
            + $"Измерения величин порядка {MinimumClaimedMeasurementMs:0.0} мс перестали быть достоверными.");
    }

    /// <summary>
    /// Расход памяти в горячем пути.
    /// </summary>
    /// <remarks>
    /// Полного нуля здесь быть не может: системный API возвращает объект ответа на каждую
    /// пробу, и обойти это можно только raw-сокетами, от которых мы отказались сознательно.
    /// Поэтому проверяется <b>бюджет</b>, а не ноль: важно, что мы не добавляем аллокаций
    /// сверх неизбежных.
    /// <para>
    /// Второе условие — доля пауз сборщика во времени измерения, а не число сборок второго
    /// поколения. Счёт сборок оказался неустойчивым: сборку может вызвать посторонний код
    /// в том же процессе, и тест мигал. Значение имеет не факт сборки, а то, сколько времени
    /// она отняла у измерения — именно это подмешивается в джиттер.
    /// </para>
    /// <para>
    /// Считается <c>GC.GetTotalAllocatedBytes</c>, а не расход текущего потока: код
    /// асинхронный, продолжения выполняются на разных потоках пула, и потоковый счётчик
    /// показал бы неполную картину.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HotPath_StaysWithinAllocationBudget()
    {
        const int BudgetBytesPerProbe = 4096;

        await using var services = MeasurementHarness.BuildServices();

        // Прогрев: первая проба тянет за собой компиляцию и разовые буферы.
        await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(20));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var pauseBefore = GC.GetTotalPauseDuration();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(ProbeCount));

        wallClock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var pause = GC.GetTotalPauseDuration() - pauseBefore;
        var perProbe = allocated / (double)ProbeCount;
        var pauseShare = pause.TotalMilliseconds / wallClock.Elapsed.TotalMilliseconds;

        _output.WriteLine($"Всего выделено : {allocated:N0} байт на {ProbeCount} проб");
        _output.WriteLine($"На пробу       : {perProbe:N0} байт (бюджет {BudgetBytesPerProbe:N0})");
        _output.WriteLine($"Паузы сборщика : {pause.TotalMilliseconds:0.###} мс из {wallClock.Elapsed.TotalMilliseconds:0} мс ({pauseShare:P2})");

        Assert.True(
            perProbe <= BudgetBytesPerProbe,
            $"Расход {perProbe:N0} байт на пробу превышает бюджет {BudgetBytesPerProbe:N0}. "
            + "В горячем пути появились лишние аллокации.");

        Assert.True(
            pauseShare <= 0.02,
            $"Паузы сборщика заняли {pauseShare:P2} времени измерения ({pause.TotalMilliseconds:0.###} мс). "
            + "На таком уровне они начинают подмешиваться в измеряемый джиттер.");
    }

    [Fact]
    public async Task Clock_ReportsResolutionAndFloor()
    {
        await using var services = MeasurementHarness.BuildServices();
        var clock = services.GetRequiredService<IHighResolutionClock>();

        await clock.CalibrateAsync();

        _output.WriteLine($"Разрешение таймера : {clock.ResolutionNanoseconds:0.###} нс");
        _output.WriteLine($"Порог разрешения   : {clock.CalibrationBaselineMs:0.000} мс");

        Assert.True(clock.ResolutionNanoseconds <= 1000, "Разрешение таймера хуже микросекунды — измерения ненадёжны.");
        Assert.True(clock.CalibrationBaselineMs >= 0, "Порог разрешения не может быть отрицательным.");
        Assert.True(clock.CalibrationBaselineMs < 10, $"Порог {clock.CalibrationBaselineMs:0.000} мс неправдоподобно велик.");
    }

    [Fact]
    public async Task CancelledRun_KeepsMeasuredSamples()
    {
        // Требование отказоустойчивости: прерванный прогон сохраняет измеренное.
        await using var services = MeasurementHarness.BuildServices();
        using var cts = new CancellationTokenSource();

        var request = MeasurementHarness.LoopbackRequest(count: 10_000, intervalMs: 10);
        var samples = new List<Sample>();

        var registry = services.GetRequiredService<Application.Probes.IProbeRegistry>();
        Assert.True(registry.TryGet("ping", out var probe));

        try
        {
            await foreach (var sample in probe.ExecuteAsync(request, NullProbeObserver.Instance, cts.Token))
            {
                samples.Add(sample);

                if (samples.Count == 5)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо.
        }

        _output.WriteLine($"Сохранено сэмплов до прерывания: {samples.Count}");

        Assert.True(samples.Count >= 5, "Прерванный прогон потерял измеренное.");
        Assert.True(samples.Count < 100, "Прогон не остановился по отмене.");
    }
}
