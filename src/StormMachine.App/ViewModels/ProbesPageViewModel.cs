using StormMachine.App.Controls;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.App.ViewModels;

/// <summary>Итог одной цели в наборе.</summary>
public sealed record ScenarioTargetRow(string Target, string Mark, string Verdict, string Where);

/// <summary>Сценарий в выпадающем списке: шаблон или собранный оператором.</summary>
public sealed record ScenarioTemplateOption(string Key, string Title, string About, bool IsTemplate, Scenario? Custom = null) : IOption
{
    public override string ToString() =>
        IsTemplate ? $"{Title} — {About}" : $"{Title} · свой — {About}";

    string IOption.Caption => Title;

    string IOption.About => About;

    /// <summary>Собранный оператором сценарий помечен: он отличается от шаблона продукта.</summary>
    string? IOption.Note => IsTemplate ? null : "свой";
}

/// <summary>
/// Экран внешних проб: сценарии из цепочки шагов.
/// </summary>
/// <remarks>
/// Отвечает на вопрос, на который одиночная проба ответить не может: «работает ли это
/// целиком, и если нет — где именно сломалось». Одно число «страница открылась за 460 мс»
/// не говорит, медленно в разрешении имени, в соединении, в рукопожатии TLS или на сервере.
/// <para>
/// Несколько целей — второй вопрос: «дело в нас или в них». Пока проверена одна цель,
/// отличить поломку канала от поломки конкретного сервера нечем.
/// </para>
/// </remarks>
public sealed partial class ProbesPageViewModel : PageViewModel, ITargetAware, IDisposable
{
    private readonly ScenarioRunner _runner;
    private readonly IHighResolutionClock _clock;
    private readonly INetworkEnvironment _environment;
    private readonly IRunStore _store;
    private readonly RunnerService _operations;

    private CancellationTokenSource? _cts;
    private ActiveScenarioViewModel? _operation;

    [ObservableProperty]
    private ScenarioTemplateOption? _template;

    /// <summary>Принимает цель из палитры команд.</summary>
    public void UseTarget(string target) => Target = target;

    [ObservableProperty]
    private string _target = "example.com";

    [ObservableProperty]
    private bool _save = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _progress = string.Empty;

    [ObservableProperty]
    private string _conclusion = string.Empty;

    /// <summary>Уровень итога по набору: им выбирается знак вердикта.</summary>
    [ObservableProperty]
    private VerdictLevel _conclusionLevel = VerdictLevel.Unknown;

    [ObservableProperty]
    private string _caption = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    private readonly ScenarioLibrary _library;
    private readonly IDeviceStore _devices;

    /// <summary>Подсказки цели из инвентаря (И-24): сеть просканирована — подставляем.</summary>
    public ObservableCollection<TargetSuggestion> Suggestions { get; } = [];

    public ProbesPageViewModel(
        NavigationSection section,
        ScenarioRunner runner,
        IHighResolutionClock clock,
        INetworkEnvironment environment,
        IRunStore store,
        RunnerService operations,
        ScenarioLibrary library,
        IScenarioStore scenarios,
        IProbeRegistry registry,
        IDeviceStore devices)
        : base(section)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _library = library ?? throw new ArgumentNullException(nameof(library));

        Editor = new ScenarioEditorViewModel(
            scenarios ?? throw new ArgumentNullException(nameof(scenarios)),
            registry ?? throw new ArgumentNullException(nameof(registry)),
            RefreshScenariosAsync);

        // Шаблоны доступны сразу, ещё до первого обращения к базе: своё дольётся
        // при активации страницы.
        foreach (var template in ScenarioTemplates.All)
        {
            Templates.Add(new ScenarioTemplateOption(template.Key, template.Title, template.About, IsTemplate: true));
        }

        Template = Templates[0];

        Sets = [.. TargetSets.All.Select(t => t.Key)];
    }

    /// <summary>Шаблоны и свои сценарии: до И-24 своё отсюда было не запустить.</summary>
    public ObservableCollection<ScenarioTemplateOption> Templates { get; } = [];

    /// <summary>Конструктор сценариев мышью.</summary>
    public ScenarioEditorViewModel Editor { get; }

    /// <summary>Выбран свой сценарий — его можно открыть в конструкторе.</summary>
    public bool CanEditSelected => Template is { IsTemplate: false };

    partial void OnTemplateChanged(ScenarioTemplateOption? value) => OnPropertyChanged(nameof(CanEditSelected));

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await RefreshScenariosAsync(cancellationToken).ConfigureAwait(true);

        Suggestions.Clear();

        foreach (var suggestion in await TargetSuggestions.LoadAsync(_devices, cancellationToken).ConfigureAwait(true))
        {
            Suggestions.Add(suggestion);
        }
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Template is { Custom: { } custom })
        {
            Editor.Load(custom);
        }
    }

    /// <summary>Перечитывает список сценариев, сохраняя выбор.</summary>
    private async Task RefreshScenariosAsync(CancellationToken cancellationToken)
    {
        var selected = Template?.Key;

        var entries = await _library.ListAsync(cancellationToken).ConfigureAwait(true);

        Templates.Clear();
        foreach (var entry in entries)
        {
            Templates.Add(new ScenarioTemplateOption(entry.Key, entry.Title, entry.About, entry.IsTemplate, entry.Custom));
        }

        Template = Templates.FirstOrDefault(t => string.Equals(t.Key, selected, StringComparison.OrdinalIgnoreCase))
                   ?? Templates.FirstOrDefault();
    }

    /// <summary>Имена готовых наборов — подставляются в поле цели одним нажатием.</summary>
    public IReadOnlyList<string> Sets { get; }

    public ObservableCollection<ScenarioStepRowViewModel> Steps { get; } = [];

    public ObservableCollection<ScenarioTargetRow> Targets { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    public bool HasTargets => Targets.Count > 1;

    public bool CanStart => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync()
    {
        if (Template is null)
        {
            return;
        }

        Error = null;
        Steps.Clear();
        Targets.Clear();
        Conclusion = string.Empty;
        ConclusionLevel = VerdictLevel.Unknown;
        Note = string.Empty;
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasTargets));

        TargetSet set;

        try
        {
            set = TargetSets.Resolve(Target, _environment);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }

        if (set.Targets.Count == 0)
        {
            Error = $"Набор «{set.Key}» пуст: {set.Origin}.";
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();

        // Сценарий попадает в список длительных операций: он идёт минутами,
        // а оператор не обязан сидеть на экране, с которого его запустил.
        _operation = _operations.StartScenario(
            set.Targets.Count > 1 ? $"{Template.Title} — целей {set.Targets.Count}" : $"{Template.Title} — {set.Targets[0]}",
            _cts);

        try
        {
            if (Save)
            {
                await _store.InitializeAsync(_cts.Token).ConfigureAwait(true);
            }

            // Калибровка одна на весь набор: часы за время прогона не меняются,
            // а её результат нужен, чтобы отличить измеренное от собственного шума.
            await _clock.CalibrateAsync(_cts.Token).ConfigureAwait(true);

            Caption = set.Targets.Count > 1
                ? $"{set.Title} — {set.Origin}, целей {set.Targets.Count}"
                : set.Targets[0];

            foreach (var target in set.Targets)
            {
                _operation.SetTarget(set.Targets.Count > 1 ? target : null);

                // Библиотека решает, что это — шаблон или своё, и подставляет цель.
                var scenario = await _library.CreateAsync(Template.Key, target, _cts.Token).ConfigureAwait(true);
                var run = await RunOneAsync(scenario, set.Targets.Count > 1 ? target : null).ConfigureAwait(true);

                Targets.Add(new ScenarioTargetRow(
                    target,
                    VerdictWording.Mark(run.Level),
                    VerdictWording.Outcome(run.Level),
                    run.FirstFailure?.Name ?? "—"));

                // Итог по набору — худший из исходов: одна упавшая цель из пяти
                // остаётся отказом, а не растворяется в четырёх успешных.
                ConclusionLevel = Worse(ConclusionLevel, run.Level);
            }

            OnPropertyChanged(nameof(HasTargets));
            Conclusion = Describe(set);
        }
        catch (OperationCanceledException)
        {
            Conclusion = "Сценарий прерван.";
            ConclusionLevel = VerdictLevel.Unknown;
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Progress = string.Empty;

            if (_operation is { } operation)
            {
                operation.Finish();
                _operations.Remove(operation);
                _operation = null;
            }

            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private async Task<ScenarioRun> RunOneAsync(Scenario scenario, string? targetLabel)
    {
        var run = await _runner
            .RunAsync(scenario, Save, OnProgress, _cts!.Token)
            .ConfigureAwait(true);

        // Полоски сравниваются в пределах одной цели: у соседней цели свой масштаб,
        // и общий сделал бы быструю цель невидимой рядом с медленной.
        var longest = run.Steps.Select(s => s.PhaseMs ?? 0).DefaultIfEmpty(0).Max();

        if (targetLabel is not null)
        {
            Steps.Add(ScenarioStepRowViewModel.Separator(targetLabel));
        }

        foreach (var step in run.Steps)
        {
            Steps.Add(new ScenarioStepRowViewModel(step, longest, _clock.CalibrationBaselineMs));
        }

        OnPropertyChanged(nameof(HasSteps));

        Note = "Шаги измеряют пересекающиеся отрезки: «Страница» включает в себя и разрешение имени, "
               + "и соединение, и рукопожатие. Столбики сравнимы между собой, но не складываются — "
               + "доля считается только внутри шага.";

        return run;
    }

    /// <summary>
    /// Ход сценария приходит с потока, на котором идёт прогон.
    /// </summary>
    /// <remarks>
    /// Оркестратор зовёт этот обработчик после <c>ConfigureAwait(false)</c>, то есть
    /// со второго шага — уже из пула потоков. Присвоение свойства оттуда уводит
    /// уведомление об изменении прямо в привязку, минуя поток разметки. Для проб
    /// это сделано правильно в <c>RunnerService</c>; у сценариев обёртки не было.
    /// </remarks>
    private void OnProgress(ScenarioProgress progress) =>
        Dispatcher.UIThread.Post(() =>
        {
            Progress = progress.Finished is null
                ? $"{progress.StepIndex + 1}/{progress.StepCount} {progress.StepName}…"
                : string.Empty;

            _operation?.Report(progress);
        });

    private string Describe(TargetSet set)
    {
        if (Targets.Count <= 1)
        {
            return Targets.Count == 1 ? $"Итог: {Targets[0].Verdict}." : string.Empty;
        }

        var failed = Targets.Count(t =>
            string.Equals(t.Verdict, VerdictWording.Outcome(VerdictLevel.Fail), StringComparison.Ordinal));

        return TargetSetConclusion.Describe(Targets.Count, failed, set.Title);
    }

    /// <summary>
    /// Худший из двух исходов.
    /// </summary>
    /// <remarks>
    /// «Не оценено» слабее всех: набор, где ни одна цель не дошла до вердикта,
    /// не считается успешным.
    /// </remarks>
    private static VerdictLevel Worse(VerdictLevel current, VerdictLevel next) =>
        Rank(next) > Rank(current) ? next : current;

    private static int Rank(VerdictLevel level) => level switch
    {
        VerdictLevel.Fail => 3,
        VerdictLevel.Warn => 2,
        VerdictLevel.Pass => 1,
        _ => 0,
    };

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    /// <summary>
    /// Уход с экрана сценарий не останавливает.
    /// </summary>
    /// <remarks>
    /// Раньше здесь стояло <c>_cts?.Cancel()</c>, и переход на любой другой раздел
    /// обрывал идущий сценарий — ровно то, ради чего он и кладётся в панель операций:
    /// прогон по восьми целям идёт минутами, и оператор не обязан сидеть на экране,
    /// с которого его запустил. Остановка осталась кнопкой «Стоп» и закрытием клиента.
    /// </remarks>
    public override void Deactivate()
    {
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Подпись набора целей для поля ввода.</summary>
    [RelayCommand]
    private void UseSet(string key) => Target = key;

    public string BaselineCaption =>
        $"Порог часов {_clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс";
}
