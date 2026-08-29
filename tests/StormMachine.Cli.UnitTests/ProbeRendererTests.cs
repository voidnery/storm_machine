using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.UnitTests;

/// <summary>
/// Показ результатов пробы.
/// </summary>
/// <remarks>
/// Здесь проверяется не разметка, а утверждения: какое число названо, каким словом
/// и не приписана ли величине чужая единица. Ошибка показа не роняет прогон — она
/// сообщает оператору неверное, и обнаружить её нечем, кроме такой проверки.
/// </remarks>
public sealed class ProbeRendererTests
{
    private static ProbeDescriptor Descriptor(
        ProbeResultShape shape = ProbeResultShape.ScalarSeries,
        MeasurementUnit unit = MeasurementUnit.Milliseconds) => new()
    {
        Kind = ProbeKind.Icmp,
        Shape = shape,
        Name = "ping",
        Title = "Задержка",
        Description = "Проба для проверки показа.",
        Unit = unit,
        Methodology = Methodology.IcmpEcho,
        Parameters = [],
    };

    private static Sample Ok(int sequence, double value, string? label = null, int? group = null) => new()
    {
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = group,
    };

    private static Sample Lost(int sequence) => new()
    {
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        Value = 0,
        Status = SampleStatus.Timeout,
    };

    private static ProbeResult Result(
        IReadOnlyList<Sample> samples,
        MeasurementUnit unit = MeasurementUnit.Milliseconds) => new()
    {
        Id = Guid.NewGuid(),
        Kind = ProbeKind.Icmp,
        Target = Target.Ip("192.168.1.1"),
        Context = MeasurementConditions.Build(null, new FixedClock(0.27), Methodology.IcmpEcho),
        Unit = unit,
        Samples = samples,
        Facts = [],
        CompletedUtc = DateTimeOffset.UnixEpoch,
    };

    // ------------------------------------------------------------------ шапка

    [Fact]
    public void Header_NamesTheConditionsOperatorMeasuresUnder()
    {
        var context = MeasurementConditions.Build(null, new FixedClock(0.27), Methodology.IcmpEcho);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteHeader(Descriptor(), Target.Ip("192.168.1.1"), context, null));

        Assert.Contains("192.168.1.1", text, StringComparison.Ordinal);
        // Методика печатается со ссылкой на стандарт: отчёт без неё — просто картинка.
        Assert.Contains("ICMP Echo (RFC 792)", text, StringComparison.Ordinal);
        Assert.Contains("0.270", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Недостоверные условия названы до измерения, а не после.
    /// </summary>
    /// <remarks>
    /// На стенде виртуальный коммутатор дал p99 в 18 раз выше p50, и без предупреждения
    /// оператор припишет дрожание гипервизора своей сети. Сказать об этом надо в шапке:
    /// после прогона он уже сделает вывод.
    /// </remarks>
    [Fact]
    public void Header_WarnsBeforeMeasuringOnUntrustworthyAdapter()
    {
        var adapter = new NetworkAdapter
        {
            Id = "vswitch",
            Name = "vEthernet",
            Description = "Hyper-V",
            Kind = AdapterKind.Virtual,
        };

        var context = MeasurementConditions.Build(adapter, new FixedClock(0.27), Methodology.IcmpEcho);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteHeader(Descriptor(), Target.Ip("192.168.1.1"), context, adapter));

        Assert.Contains("ВНИМАНИЕ", text, StringComparison.Ordinal);
        Assert.Contains("виртуальный коммутатор", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_IsQuietWhenConditionsAreFine()
    {
        var adapter = new NetworkAdapter
        {
            Id = "eth0",
            Name = "Ethernet",
            Description = "Проводной",
            Kind = AdapterKind.Physical,
        };

        var context = MeasurementConditions.Build(adapter, new FixedClock(0.27), Methodology.IcmpEcho);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteHeader(Descriptor(), Target.Ip("192.168.1.1"), context, adapter));

        Assert.DoesNotContain("ВНИМАНИЕ", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ скалярный ряд

    [Fact]
    public void Summary_CountsSentReceivedAndLost()
    {
        var result = Result([Ok(0, 1.0), Ok(1, 2.0), Lost(2), Lost(3)]);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteSummary(Descriptor(), result, new FixedClock(0.27)));

        Assert.Contains("Отправлено 4", text, StringComparison.Ordinal);
        Assert.Contains("получено 2", text, StringComparison.Ordinal);
        Assert.Contains("потеряно 2", text, StringComparison.Ordinal);
        Assert.Contains("50.0%", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Без единого ответа продукт говорит об этом, а не показывает нули.
    /// </summary>
    /// <remarks>
    /// Ноль в графе «медиана» и «нечего считать» — разные утверждения, и первое
    /// оператор прочтёт как измерение.
    /// </remarks>
    [Fact]
    public void Summary_SaysThereIsNothingToComputeRatherThanShowingZeroes()
    {
        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteSummary(Descriptor(), Result([Lost(0), Lost(1)]), new FixedClock(0.27)));

        Assert.Contains("статистику посчитать не по чему", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Перцентили", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Скорость не подписывается миллисекундами и не получает джиттер.
    /// </summary>
    /// <remarks>
    /// Джиттер и PDV — понятия о задержке. Посчитать их по ряду скоростей можно,
    /// истолковать нельзя, и показывать такое число значит предложить оператору
    /// вывод, которого нет.
    /// </remarks>
    [Fact]
    public void Summary_DoesNotDescribeRateInTermsOfLatency()
    {
        var result = Result(
            [Ok(0, 94.2), Ok(1, 91.8), Ok(2, 95.0)],
            MeasurementUnit.MegabitsPerSecond);

        var text = ConsoleCapture.Of(() => ProbeRenderer.WriteSummary(
            Descriptor(unit: MeasurementUnit.MegabitsPerSecond),
            result,
            new FixedClock(0.27)));

        Assert.Contains("Мбит/с", text, StringComparison.Ordinal);
        Assert.Contains("Отсчётов: 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RTT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("потеряно", text, StringComparison.Ordinal);

        // Джиттер и PDV не просто отсутствуют — продукт объясняет, почему их нет.
        Assert.DoesNotContain("Джиттер    ", text, StringComparison.Ordinal);
        Assert.Contains("к ряду скоростей они неприменимы", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Медиана на уровне порога разрешения — повод предупредить, а не промолчать.
    /// </summary>
    /// <remarks>
    /// Ниже калибровочного базиса различить сеть и накладные расходы стека нельзя,
    /// и число там означает не «быстро», а «неизвестно».
    /// </remarks>
    [Fact]
    public void Summary_WarnsWhenValuesSitAtTheResolutionFloor()
    {
        var result = Result([Ok(0, 0.05), Ok(1, 0.06), Ok(2, 0.05)]);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteSummary(Descriptor(), result, new FixedClock(0.27)));

        Assert.Contains("на уровне порога разрешения", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_IsQuietWhenValuesAreWellAboveTheFloor()
    {
        var result = Result([Ok(0, 12.0), Ok(1, 13.0), Ok(2, 11.5)]);

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteSummary(Descriptor(), result, new FixedClock(0.27)));

        Assert.DoesNotContain("порога разрешения", text, StringComparison.Ordinal);
    }

    /// <summary>Прерванный прогон честно говорит, что показанное — неполно.</summary>
    [Fact]
    public void Summary_SaysWhenTheRunWasCutShort()
    {
        var result = Result([Ok(0, 1.0)]) with { WasCancelled = true };

        var text = ConsoleCapture.Of(() =>
            ProbeRenderer.WriteSummary(Descriptor(), result, new FixedClock(0.27)));

        Assert.Contains("прерван", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- водопад фаз

    /// <summary>Водопад нужен ради одного вывода: какая фаза съедает время.</summary>
    [Fact]
    public void Waterfall_NamesTheSlowestPhase()
    {
        var result = Result(
        [
            Ok(0, 5.0, "dns", 0),
            Ok(1, 30.0, "connect", 0),
            Ok(2, 180.0, "tls", 0),
            Ok(3, 20.0, "ttfb", 0),
        ]);

        var text = ConsoleCapture.Of(() => ProbeRenderer.WriteSummary(
            Descriptor(ProbeResultShape.PhasedTiming),
            result,
            new FixedClock(0.27)));

        Assert.Contains("Больше всего времени занимает фаза «TLS»", text, StringComparison.Ordinal);
        Assert.Contains("ИТОГО", text, StringComparison.Ordinal);
        Assert.Contains("235.000", text, StringComparison.Ordinal);
    }

    /// <summary>Провалившаяся попытка называет фазу, на которой встала.</summary>
    [Fact]
    public void Waterfall_NamesThePhaseThatFailed()
    {
        var failed = Lost(0) with { Label = "tls", Group = 0 };

        var text = ConsoleCapture.Of(() => ProbeRenderer.WriteSummary(
            Descriptor(ProbeResultShape.PhasedTiming),
            Result([failed]),
            new FixedClock(0.27)));

        Assert.Contains("таймаут", text, StringComparison.Ordinal);
        Assert.Contains("«tls»", text, StringComparison.Ordinal);
    }

    /// <summary>Часы с заданным калибровочным базисом — всё, что нужно показу.</summary>
    private sealed class FixedClock(double baselineMs) : IHighResolutionClock
    {
        public double ResolutionNanoseconds => 100;

        public double CalibrationBaselineMs { get; } = baselineMs;

        public long GetTimestamp() => 0;

        public double ElapsedMilliseconds(long startTimestamp) => 0;

        public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) => 0;

        public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
