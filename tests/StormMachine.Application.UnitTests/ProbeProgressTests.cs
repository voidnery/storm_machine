using System.Runtime.CompilerServices;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Ход подготовки доходит до клиента.
/// </summary>
/// <remarks>
/// Написано по находке И-19. Пробы агента сообщали «жду звонка, набери на его машине
/// вот это» через <c>Console.WriteLine</c>. В консоли это работало, а графический клиент
/// собран как <c>WinExe</c> и консоли не имеет — указание пропадало, и прогон молча стоял
/// до истечения срока ожидания. Оператор не знал ни что от него требуется, ни почему
/// ничего не происходит.
/// <para>
/// Отсюда требование: сообщение обязано пройти <b>через наблюдателя</b>, а не мимо него, —
/// иначе клиент, у которого консоли нет, снова его не увидит.
/// </para>
/// </remarks>
public sealed class ProbeProgressTests
{
    [Fact]
    public void Collector_ForwardsProgressImmediately()
    {
        var seen = new List<string>();
        var collector = new ProbeCollector(seen.Add);

        collector.OnProgress("жду звонка");

        Assert.Equal(["жду звонка"], seen);
    }

    [Fact]
    public void Collector_WithoutHandler_IgnoresProgress()
    {
        var collector = new ProbeCollector();

        collector.OnProgress("никто не слушает");

        Assert.Empty(collector.Facts);
    }

    /// <summary>Ход подготовки — не факт: в журнале ему делать нечего.</summary>
    [Fact]
    public void Progress_DoesNotBecomeAFact()
    {
        var collector = new ProbeCollector(_ => { });

        collector.OnProgress("жду звонка");

        Assert.Empty(collector.Facts);
    }

    /// <summary>
    /// Оркестратор проводит ход подготовки от пробы к клиенту.
    /// </summary>
    /// <remarks>
    /// Проверяется весь путь целиком, а не только сборщик: дефект был именно в том,
    /// что путь обрывался, хотя оба его конца существовали.
    /// </remarks>
    [Fact]
    public async Task Orchestrator_DeliversProgressToClient()
    {
        var seen = new List<string>();

        var orchestrator = new RunOrchestrator(
            new NullRunStore(),
            new NullClock(),
            new NullEnvironment());

        await orchestrator.RunAsync(
            new TalkingProbe(),
            new ProbeRequest { Target = Target.Ip("127.0.0.1"), Parameters = new Dictionary<string, object?>() },
            new RunOptions { OnProgress = seen.Add });

        Assert.Equal([TalkingProbe.Message], seen);
    }

    /// <summary>Проба, которая перед измерением просит оператора кое-что сделать.</summary>
    private sealed class TalkingProbe : IProbe
    {
        public const string Message = "Жду звонка агента «стенд» на порт 7431.";

        public ProbeDescriptor Descriptor { get; } = new()
        {
            Kind = ProbeKind.Icmp,
            Shape = ProbeResultShape.ScalarSeries,
            Name = "talking",
            Title = "Проба, которая предупреждает",
            Description = "Сообщает о ходе подготовки до первого измерения.",
            Unit = MeasurementUnit.Milliseconds,
            Methodology = Methodology.IcmpEcho,
            Parameters = [],
        };

        public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request) => [];

        public async IAsyncEnumerable<Sample> ExecuteAsync(
            ProbeRequest request,
            IProbeObserver observer,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            observer.OnProgress(Message);

            yield return new Sample
            {
                Sequence = 0,
                TimestampUtc = DateTimeOffset.UtcNow,
                Value = 1,
                Status = SampleStatus.Success,
            };

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
