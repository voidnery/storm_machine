using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Text;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Общие для всех клиентов формулировки.
/// </summary>
/// <remarks>
/// Собраны в И-19 из копий, разошедшихся по консоли, графическому клиенту и отчёту.
/// Копий было восемь, тестов на них — ни одного: правило, написанное четыре раза,
/// четыре раза и проверялось глазами.
/// </remarks>
public sealed class SharedWordingTests
{
    [Theory]
    [InlineData(1, "шаг")]
    [InlineData(2, "шага")]
    [InlineData(4, "шага")]
    [InlineData(5, "шагов")]
    [InlineData(0, "шагов")]
    [InlineData(21, "шаг")]
    [InlineData(22, "шага")]
    [InlineData(101, "шаг")]
    [InlineData(104, "шага")]
    public void Plural_FollowsTheLastDigit(int count, string expected) =>
        Assert.Equal(expected, Plural.Of(count, "шаг", "шага", "шагов"));

    /// <summary>Вторая десятка целиком идёт в последнюю форму — вопреки последней цифре.</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(111)]
    [InlineData(112)]
    public void Plural_TreatsTheTeensApart(int count) =>
        Assert.Equal("шагов", Plural.Of(count, "шаг", "шага", "шагов"));

    /// <summary>
    /// Отрицательное число не должно давать «-2 шагов».
    /// </summary>
    /// <remarks>
    /// В предметной области отрицательных счётчиков нет, но вычитание, ушедшее в минус,
    /// однажды случится, и грамматика не должна ломаться там, где ломается арифметика.
    /// </remarks>
    [Fact]
    public void Plural_SurvivesNegativeCount() =>
        Assert.Equal("шага", Plural.Of(-2, "шаг", "шага", "шагов"));

    [Fact]
    public void PluralWith_PutsTheNumberFirst() =>
        Assert.Equal("3 шага", Plural.With(3, "шаг", "шага", "шагов"));

    [Fact]
    public void Verdict_MarkAndWordsAreDistinctVocabularies()
    {
        // «в норме» — об измерении, которое закончилось; «норма» — о состоянии
        // монитора прямо сейчас. Разные вопросы, и сводить их в одно слово нельзя.
        Assert.Equal("в норме", VerdictWording.Outcome(VerdictLevel.Pass));
        Assert.Equal("норма", VerdictWording.State(VerdictLevel.Pass));
        Assert.Equal("✓", VerdictWording.Mark(VerdictLevel.Pass));
    }

    [Fact]
    public void Verdict_UnknownStateIsNamedByTheCaller()
    {
        // Монитор, который ни разу не работал, и проверка без порогов — для оператора
        // это разные вещи, и общее слово за него подставлять неправильно.
        Assert.Equal("неизвестно", VerdictWording.State(VerdictLevel.Unknown));
        Assert.Equal("ещё не проверялся", VerdictWording.State(VerdictLevel.Unknown, "ещё не проверялся"));
    }

    [Fact]
    public void TargetSet_AllFailedPointsAtTheCommonPath()
    {
        var text = TargetSetConclusion.Describe(total: 4, failed: 4);

        Assert.Contains("упали все 4 цели", text, StringComparison.Ordinal);
        Assert.Contains("Общая часть пути", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetSet_SomeFailedClearsThePath()
    {
        var text = TargetSetConclusion.Describe(total: 4, failed: 1);

        Assert.Contains("упало 1 из 4", text, StringComparison.Ordinal);
        Assert.Contains("дело в упавших, а не в канале", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetSet_NoneFailedSaysSoPlainly() =>
        Assert.Contains("ни одна цель не упала", TargetSetConclusion.Describe(4, 0), StringComparison.Ordinal);

    [Fact]
    public void TargetSet_EmptySetDoesNotPretendToConclude() =>
        Assert.Contains("проверять было нечего", TargetSetConclusion.Describe(0, 0), StringComparison.Ordinal);

    [Fact]
    public void TargetSet_UsesTheSetTitleWhenThereIsOne()
    {
        Assert.Contains("«Внешние службы»", TargetSetConclusion.Describe(3, 0, "Внешние службы"), StringComparison.Ordinal);
        Assert.DoesNotContain("«", TargetSetConclusion.Describe(3, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Один и тот же исход объясняется одинаково, откуда бы ни спросили.
    /// </summary>
    /// <remarks>
    /// Ради этого вывод и вынесен из клиентов: до И-19 консоль и окно хранили копии
    /// текста, и разойтись им ничего не мешало.
    /// </remarks>
    [Fact]
    public void TargetSet_SameOutcomeReadsTheSameEverywhere()
    {
        var console = TargetSetConclusion.Describe(4, 4);
        var window = TargetSetConclusion.Describe(4, 4);

        Assert.Equal(console, window);
    }
}
