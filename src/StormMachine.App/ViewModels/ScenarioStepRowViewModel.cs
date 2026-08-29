using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.App.ViewModels;

/// <summary>Составляющая шага: фаза водопада или ряд сравнения.</summary>
public sealed record ScenarioPartRow(string Label, string Value, double Share, bool ShowShare)
{
    public string ShareText => ShowShare
        ? Share.ToString("P0", CultureInfo.InvariantCulture)
        : string.Empty;

    public double BarWidth => Math.Max(2, Share * 240);
}

/// <summary>
/// Строка шага сценария на экране.
/// </summary>
/// <remarks>
/// Длина полоски считается от измеренного значения, а не от времени шага. Время шага
/// определяется числом проб и паузой между ними — полоска по нему сравнивала бы
/// настройки замера, а не фазы.
/// </remarks>
public sealed partial class ScenarioStepRowViewModel : ObservableObject
{
    private const double BarPixels = 260;

    public ScenarioStepRowViewModel(ScenarioStepResult step, double longestMs, double baselineMs)
    {
        ArgumentNullException.ThrowIfNull(step);

        Name = step.Name;
        Level = step.Verdict.Level;
        Mark = VerdictWording.Mark(Level);

        Summary = step.Verdict.Summary;
        Explanation = step.Verdict.Explanation;
        Warnings = [.. step.Warnings.Select(w => $"{w.Name}: {w.Value}")];
        RunId = step.RunId;

        Measured = step.WasSkipped
            ? "пропущен"
            : step.PhaseMs is { } ms
                ? Format(ms, baselineMs)
                : "—";

        BarWidth = step.PhaseMs is { } value && longestMs > 0
            ? Math.Max(2, value / longestMs * BarPixels)
            : 0;

        Parts = BuildParts(step, baselineMs);
    }

    private ScenarioStepRowViewModel(string target)
    {
        Name = target;
        IsSeparator = true;
        Mark = string.Empty;
        Measured = string.Empty;
        Summary = string.Empty;
        Warnings = [];
        Parts = [];
    }

    /// <summary>
    /// Разделитель между целями набора.
    /// </summary>
    /// <remarks>
    /// Нужен именно в списке шагов: при нескольких целях шаги идут подряд, и без подписи
    /// «Соединение» четвёртой цели читалось бы как четвёртый шаг первой.
    /// </remarks>
    public static ScenarioStepRowViewModel Separator(string target) => new(target);

    public bool IsSeparator { get; }

    public bool IsStep => !IsSeparator;

    public string Name { get; }

    public VerdictLevel Level { get; }

    public string Mark { get; }

    public string Measured { get; }

    public double BarWidth { get; }

    public string Summary { get; }

    public string? Explanation { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public Guid? RunId { get; }

    public IReadOnlyList<ScenarioPartRow> Parts { get; }

    public bool HasParts => Parts.Count > 0;

    public string PartsCaption { get; private set; } = string.Empty;

    private IReadOnlyList<ScenarioPartRow> BuildParts(ScenarioStepResult step, double baselineMs)
    {
        if (step.Series.Count < 2)
        {
            return [];
        }

        var measured = step.Series.Where(s => s.Statistics.SampleCount > 0).ToList();

        // Доля осмысленна только у фаз: они идут подряд и складываются в шаг целиком.
        // Ряды сравнения идут параллельно и не складываются ни во что — им доля
        // приписала бы сумму, которой не существует.
        if (step.Shape == ProbeResultShape.ComparedSeries)
        {
            PartsCaption = "сравнение рядов — от быстрого к медленному";

            return
            [
                .. measured
                    .OrderBy(s => s.Statistics.P50Ms)
                    .Select(s => new ScenarioPartRow(
                        s.Label,
                        Format(s.Statistics.P50Ms, baselineMs),
                        Normalize(s.Statistics.P50Ms, measured.Max(m => m.Statistics.P50Ms)),
                        ShowShare: false)),
            ];
        }

        PartsCaption = "фазы шага — они складываются в него целиком";
        var total = measured.Sum(s => s.Statistics.P50Ms);

        return
        [
            .. measured.Select(s => new ScenarioPartRow(
                s.Label,
                Format(s.Statistics.P50Ms, baselineMs),
                total > 0 ? s.Statistics.P50Ms / total : 0,
                ShowShare: true)),
        ];
    }

    private static double Normalize(double value, double largest) => largest > 0 ? value / largest : 0;

    /// <summary>
    /// Значение с оглядкой на порог достоверности часов.
    /// </summary>
    /// <remarks>
    /// «0.0 мс» читается как «мгновенно», а означает «короче, чем измеритель различает».
    /// Разница существенная: в первом случае фазы нет, во втором её длительность неизвестна.
    /// </remarks>
    private static string Format(double ms, double baselineMs) =>
        ms > 0 && ms < baselineMs
            ? $"< {baselineMs.ToString("0.0", CultureInfo.InvariantCulture)} мс"
            : ms < 10
                ? $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} мс"
                : $"{ms.ToString("0", CultureInfo.InvariantCulture)} мс";
}
