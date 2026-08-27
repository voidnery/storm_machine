using System.Diagnostics;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки трассировки и непрерывного режима.
/// </summary>
/// <remarks>
/// Цель — loopback: он отвечает на первом же TTL, поэтому длина маршрута известна заранее
/// и число сэмплов можно посчитать точно. Это не проверка сети — это проверка того,
/// что циклы наблюдения идут по разведанному маршруту, а не пересканируют его заново.
/// </remarks>
public sealed class TracerouteProbeTests
{
    /// <summary>
    /// Таймер поверх <see cref="Stopwatch"/> без калибровки.
    /// </summary>
    /// <remarks>
    /// Заглушка с постоянным нулём здесь не годится: выдерживание интервала между
    /// циклами дожидается момента активным ожиданием, и на неподвижном таймере оно
    /// не закончится никогда. Нужен именно идущий таймер — просто без калибровки,
    /// которая в тестах только тратит время.
    /// </remarks>
    private sealed class TestClock : IHighResolutionClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public double ResolutionNanoseconds => 1_000_000_000.0 / Stopwatch.Frequency;

        public double CalibrationBaselineMs => 0;

        public long GetTimestamp() => _watch.ElapsedTicks;

        public double ElapsedMilliseconds(long startTimestamp) =>
            ElapsedMilliseconds(startTimestamp, _watch.ElapsedTicks);

        public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) =>
            (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public bool IsElevated => false;

        public IReadOnlyList<NetworkAdapter> GetAdapters() => [];

        public NetworkAdapter? GetPrimaryAdapter() => null;
    }

    private sealed class FakeAnnotator : IHopAnnotator
    {
        public bool HasAsnData => true;

        public string AsnDatabaseHint => "тест";

        public string? Attribution => "тестовый источник";

        public List<string> Requested { get; } = [];

        public Task<IReadOnlyDictionary<string, HopAnnotation>> AnnotateAsync(
            IReadOnlyList<string> addresses,
            CancellationToken cancellationToken = default)
        {
            Requested.AddRange(addresses);

            IReadOnlyDictionary<string, HopAnnotation> result = addresses.ToDictionary(
                a => a,
                a => new HopAnnotation { Address = a, IsPrivate = true },
                StringComparer.Ordinal);

            return Task.FromResult(result);
        }
    }

    private static TracerouteProbe CreateProbe(IHopAnnotator? annotator = null) =>
        new(new TestClock(), new TargetResolver(new FakeEnvironment()), annotator);

    private static ProbeRequest Loopback(int rounds, int attempts = 1, int intervalMs = 100) => new()
    {
        Target = Target.Parse("127.0.0.1"),
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["max-hops"] = 5,
            ["attempts"] = attempts,
            ["rounds"] = rounds,
            ["interval"] = intervalMs,
            ["timeout"] = 500,
        },
    };

    private static async Task<(List<Sample> Samples, ProbeCollector Observer)> RunAsync(
        TracerouteProbe probe,
        ProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var observer = new ProbeCollector();
        var samples = new List<Sample>();

        await foreach (var sample in probe.ExecuteAsync(request, observer, cancellationToken))
        {
            samples.Add(sample);
        }

        return (samples, observer);
    }

    [Fact]
    public void DeclaresContinuousModeParameters()
    {
        var names = CreateProbe().Descriptor.Parameters.Select(p => p.Name).ToList();

        Assert.Contains("rounds", names);
        Assert.Contains("interval", names);
    }

    [Fact]
    public async Task SingleRun_ProbesOnlyTheDiscoveredPath()
    {
        // Loopback отвечает на первом TTL — разведка обязана остановиться там же,
        // а не досылать пакеты на оставшиеся значения TTL.
        var (samples, observer) = await RunAsync(CreateProbe(), Loopback(rounds: 1, attempts: 3));

        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal(1, s.Group));
        Assert.Contains(observer.Facts, f => f.Value.Contains("достигнута", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuousMode_AddsOnePacketPerHopPerRound()
    {
        const int Rounds = 4;
        const int Attempts = 2;

        var (samples, _) = await RunAsync(CreateProbe(), Loopback(Rounds, Attempts));

        // Разведка: attempts пакетов на единственный хоп. Дальше по одному пакету за цикл.
        Assert.Equal(Attempts + (Rounds - 1), samples.Count);
        Assert.All(samples, s => Assert.Equal(1, s.Group));
    }

    [Fact]
    public async Task ContinuousMode_NumbersSamplesWithoutGaps()
    {
        var (samples, _) = await RunAsync(CreateProbe(), Loopback(rounds: 3, attempts: 2));

        Assert.Equal(Enumerable.Range(0, samples.Count), samples.Select(s => s.Sequence));
    }

    [Fact]
    public async Task ContinuousMode_ReportsModeAsFact()
    {
        var (_, observer) = await RunAsync(CreateProbe(), Loopback(rounds: 3));

        Assert.Contains(observer.Facts, f => f.Name == "Режим" && f.Value.Contains('3'));
    }

    [Fact]
    public async Task SingleRun_DoesNotReportContinuousMode()
    {
        var (_, observer) = await RunAsync(CreateProbe(), Loopback(rounds: 1));

        Assert.DoesNotContain(observer.Facts, f => f.Name == "Режим");
    }

    [Fact]
    public async Task Annotator_IsAskedAboutRespondingAddressesOnly()
    {
        var annotator = new FakeAnnotator();

        var (_, observer) = await RunAsync(CreateProbe(annotator), Loopback(rounds: 2, attempts: 2));

        Assert.Equal(["127.0.0.1"], annotator.Requested);

        // Частный адрес аннотировать нечем — подпись в таблицу не попадает,
        // а указание источника попадает всегда, когда база есть.
        Assert.Contains(observer.Facts, f => f.Name == "Источник данных");
    }

    [Fact]
    public async Task WithoutAnnotator_StillProducesResult()
    {
        var (samples, observer) = await RunAsync(CreateProbe(), Loopback(rounds: 2));

        Assert.NotEmpty(samples);
        Assert.DoesNotContain(observer.Facts, f => f.Category == HopAnnotation.FactCategory);
    }

    [Fact]
    public async Task Cancellation_KeepsWhatWasMeasured()
    {
        using var cancellation = new CancellationTokenSource();

        var probe = CreateProbe();
        var observer = new ProbeCollector();
        var samples = new List<Sample>();

        try
        {
            await foreach (var sample in probe.ExecuteAsync(
                               Loopback(rounds: 1000, intervalMs: 100),
                               observer,
                               cancellation.Token))
            {
                samples.Add(sample);

                if (samples.Count == 3)
                {
                    await cancellation.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена — штатный выход.
        }

        Assert.True(samples.Count >= 3, "Прерванная трассировка потеряла измеренное.");
        Assert.True(samples.Count < 100, "Трассировка не остановилась по отмене.");

        // Непрерывное наблюдение останавливают вручную — это штатный способ его
        // закончить. Итог обязан подводиться и в этом случае, иначе час наблюдения
        // остался бы без вывода.
        Assert.Contains(observer.Facts, f => f.Name == "Наблюдение");
        Assert.Contains(observer.Facts, f => f.Name == "Итог");
    }

    [Fact]
    public async Task Cancellation_IsStillReportedToTheCaller()
    {
        // Оркестратор отличает прерванный прогон от завершённого по исключению.
        // Подведение итога не должно это исключение проглотить.
        using var cancellation = new CancellationTokenSource();

        var probe = CreateProbe();
        var samples = new List<Sample>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var sample in probe.ExecuteAsync(
                               Loopback(rounds: 1000, intervalMs: 100),
                               new ProbeCollector(),
                               cancellation.Token))
            {
                samples.Add(sample);

                if (samples.Count == 2)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }

    [Fact]
    public async Task PathAnalysis_ReadsWhatTheProbeProduced()
    {
        // Сквозная проверка: проба и разбор договорились о том, что лежит в Group
        // и RespondedBy. Разойдись они — таблица маршрута опустела бы молча.
        var (samples, observer) = await RunAsync(CreateProbe(), Loopback(rounds: 3, attempts: 2));

        var analysis = PathAnalysis.Compute(samples, observer.ResolvedAddress);

        var hop = Assert.Single(analysis.Hops);
        Assert.Equal(1, hop.Hop);
        Assert.Equal("127.0.0.1", hop.Address);
        Assert.True(hop.IsDestination);
        Assert.True(analysis.DestinationReached);
        Assert.Empty(analysis.RouteChanges);
    }
}
