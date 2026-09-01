using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.App.ViewModels;

/// <summary>Один шаг в конструкторе.</summary>
public sealed partial class ScenarioStepDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _probeName = "ping";

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Параметры пробы: «count=4 interval=200», через пробел.</summary>
    [ObservableProperty]
    private string _parametersText = string.Empty;

    /// <summary>Пороги: «p95 &lt; 100; потери &lt;= 0», через точку с запятой.</summary>
    [ObservableProperty]
    private string _thresholdsText = string.Empty;
}

/// <summary>
/// Конструктор сценариев мышью.
/// </summary>
/// <remarks>
/// Последний пункт состава И-24: цепочка собиралась только консолью
/// (<c>storm scenario new/step</c>), а экран предлагал лишь готовые шаблоны.
/// Проверка шагов та же, что в консоли, и происходит при сохранении, а не при
/// запуске: сценарий с непригодным параметром падал бы посреди прогона,
/// потратив время на предыдущие шаги.
/// </remarks>
public sealed partial class ScenarioEditorViewModel : ObservableObject
{
    private readonly IScenarioStore _store;
    private readonly IProbeRegistry _registry;
    private readonly Func<CancellationToken, Task> _changed;

    public ScenarioEditorViewModel(
        IScenarioStore store,
        IProbeRegistry registry,
        Func<CancellationToken, Task> changed)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));

        ProbeNames = [.. registry.Descriptors.Select(d => d.Name)];
    }

    /// <summary>Имена проб для выпадающего списка шага — из того же реестра, что и консоль.</summary>
    public IReadOnlyList<string> ProbeNames { get; }

    public ObservableCollection<ScenarioStepDraftViewModel> Steps { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    public static string Note =>
        "Параметры пишутся через пробел: count=4 interval=200. Пороги — через точку "
        + "с запятой: p95 < 100; потери <= 0. Пустая цель шага наследует цель предыдущего.";

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void AddStep()
    {
        Steps.Add(new ScenarioStepDraftViewModel { Target = Steps.LastOrDefault()?.Target ?? string.Empty });
        OnPropertyChanged(nameof(HasSteps));
    }

    [RelayCommand]
    private void RemoveStep(ScenarioStepDraftViewModel draft)
    {
        Steps.Remove(draft);
        OnPropertyChanged(nameof(HasSteps));
    }

    [RelayCommand]
    private void MoveUp(ScenarioStepDraftViewModel draft) => Move(draft, -1);

    [RelayCommand]
    private void MoveDown(ScenarioStepDraftViewModel draft) => Move(draft, +1);

    /// <summary>Открывает свой сценарий в редакторе.</summary>
    public void Load(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        Name = scenario.Name;
        Description = scenario.Description;
        Message = Error = null;

        Steps.Clear();
        foreach (var step in scenario.Steps)
        {
            Steps.Add(new ScenarioStepDraftViewModel
            {
                ProbeName = step.ProbeName,
                Target = step.Target.Value,
                Title = step.Name,
                ParametersText = string.Join(' ', step.Parameters.Select(p =>
                    $"{p.Key}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}")),
                ThresholdsText = string.Join("; ", step.Thresholds.Select(t => t.Describe())),
            });
        }

        OnPropertyChanged(nameof(HasSteps));
        IsOpen = true;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Message = Error = null;

        var title = Name.Trim();

        if (title.Length == 0)
        {
            Error = "У сценария должно быть имя — по нему его запускают.";

            return;
        }

        if (Steps.Count == 0)
        {
            Error = "В сценарии нет ни одного шага — проверять нечего.";

            return;
        }

        var (steps, problems) = BuildSteps();

        if (problems.Count > 0)
        {
            Error = string.Join(Environment.NewLine, problems);

            return;
        }

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

        // Существующий сценарий обновляется с поднятой редакцией: прогоны разных
        // редакций несравнимы, и номер — единственный след того, что цепочка менялась.
        var existing = await _store.FindAsync(title, cancellationToken).ConfigureAwait(true);

        await _store.SaveAsync(
            new Scenario
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = title,
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                Steps = steps,
                CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Version = (existing?.Version ?? 0) + 1,
            },
            cancellationToken).ConfigureAwait(true);

        Message = existing is null
            ? $"Сценарий «{title}» сохранён: шагов {steps.Count}. Он уже в списке запуска слева."
            : $"Сценарий «{title}» обновлён (редакция {existing.Version + 1}): шагов {steps.Count}.";

        await _changed(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        Message = Error = null;

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

        var existing = await _store.FindAsync(Name.Trim(), cancellationToken).ConfigureAwait(true);

        if (existing is null)
        {
            Error = $"Своего сценария «{Name.Trim()}» нет. Шаблоны удалить нельзя — они часть продукта.";

            return;
        }

        await _store.DeleteAsync(existing.Id, cancellationToken).ConfigureAwait(true);

        Message = $"Сценарий «{existing.Name}» удалён.";

        await _changed(cancellationToken).ConfigureAwait(true);
    }

    private (List<ScenarioStep> Steps, List<string> Problems) BuildSteps()
    {
        var steps = new List<ScenarioStep>();
        var problems = new List<string>();

        for (var i = 0; i < Steps.Count; i++)
        {
            var draft = Steps[i];
            var where = $"Шаг {i + 1}";

            if (!_registry.TryGet(draft.ProbeName, out var probe))
            {
                problems.Add($"{where}: проба «{draft.ProbeName}» не зарегистрирована.");

                continue;
            }

            Target target;

            try
            {
                // Пустая цель наследует предыдущую — та же логика, что в консоли:
                // сценарий к одному узлу не должен требовать цель у каждого шага.
                var text = draft.Target.Trim();
                target = text.Length > 0
                    ? Target.Parse(text)
                    : steps.Count > 0 ? steps[^1].Target : Target.Parse("127.0.0.1");
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{where}: {ex.Message}");

                continue;
            }

            List<Threshold> thresholds = [];

            foreach (var piece in draft.ThresholdsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    thresholds.Add(Threshold.Parse(piece));
                }
                catch (FormatException ex)
                {
                    problems.Add($"{where}: {ex.Message}");
                }
            }

            var step = new ScenarioStep
            {
                Name = string.IsNullOrWhiteSpace(draft.Title) ? probe.Descriptor.Title : draft.Title.Trim(),
                ProbeName = draft.ProbeName,
                Target = target,
                Parameters = ParseParameters(draft.ParametersText),
                Thresholds = thresholds,
            };

            foreach (var error in probe.Validate(new ProbeRequest { Target = step.Target, Parameters = step.Parameters }))
            {
                problems.Add($"{where}, параметр {error.ParameterName}: {error.Message}");
            }

            steps.Add(step);
        }

        return (steps, problems);
    }

    /// <summary>
    /// Разбирает «count=4 interval=200».
    /// </summary>
    /// <remarks>
    /// Числа разбираются числами, как в консоли: проба объявляет типы параметров,
    /// и строка «4» там, где ждут целое, до неё не доедет.
    /// </remarks>
    private static Dictionary<string, object?> ParseParameters(string text)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var piece in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = piece.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                continue;
            }

            var name = piece[..split].Trim();
            var value = piece[(split + 1)..].Trim();

            parameters[name] = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    ? real
                    : value;
        }

        return parameters;
    }

    private void Move(ScenarioStepDraftViewModel draft, int shift)
    {
        var index = Steps.IndexOf(draft);
        var destination = index + shift;

        if (index < 0 || destination < 0 || destination >= Steps.Count)
        {
            return;
        }

        Steps.Move(index, destination);
    }
}
