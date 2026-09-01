using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Controls;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Одна проба в выпадающем списке прогонщика.</summary>
public sealed record ProbeOption(IProbe Probe)
{
    public ProbeDescriptor Descriptor => Probe.Descriptor;

    public override string ToString() => $"{Descriptor.Title} — {Descriptor.Description}";
}

/// <summary>Поле формы, построенное из объявления параметра пробы.</summary>
public sealed partial class ParameterFieldViewModel : ObservableObject
{
    public ParameterFieldViewModel(ProbeParameter parameter)
    {
        Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));

        switch (parameter.Type)
        {
            case ProbeParameterType.Boolean:
                Flag = parameter.DefaultValue is true;
                break;

            case ProbeParameterType.Text:
            case ProbeParameterType.Choice:
                Text = Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty;
                break;

            default:
                Number = parameter.DefaultValue is null
                    ? null
                    : Convert.ToDecimal(parameter.DefaultValue, CultureInfo.InvariantCulture);
                break;
        }
    }

    public ProbeParameter Parameter { get; }

    public string Label => Parameter.Label;

    public string? Description => Parameter.Description;

    public bool IsNumber => Parameter.Type
        is ProbeParameterType.Integer or ProbeParameterType.Decimal or ProbeParameterType.Duration;

    public bool IsBoolean => Parameter.Type is ProbeParameterType.Boolean;

    public bool IsChoice => Parameter.Type is ProbeParameterType.Choice;

    public bool IsText => Parameter.Type is ProbeParameterType.Text;

    public IReadOnlyList<string> Choices => Parameter.Choices ?? [];

    public decimal Minimum => (decimal)(Parameter.Minimum ?? 0);

    public decimal Maximum => (decimal)(Parameter.Maximum ?? double.MaxValue);

    [ObservableProperty]
    private decimal? _number;

    [ObservableProperty]
    private bool _flag;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Значение для запроса пробы — типом, который проба объявила.</summary>
    public object? Value => Parameter.Type switch
    {
        ProbeParameterType.Boolean => Flag,
        ProbeParameterType.Text or ProbeParameterType.Choice =>
            string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
        ProbeParameterType.Decimal => Number is { } real ? (double)real : null,
        _ => Number is { } number ? (int)Math.Round(number) : null,
    };
}

/// <summary>
/// Прогонщик пробы: форма из паспорта, запуск, итог с рядами и фактами.
/// </summary>
/// <remarks>
/// Принцип 1 анализа доехал до экрана (И-24): проба объявляет параметры декларативно,
/// и форма строится по объявлению — как команда консоли в <c>ProbeCommandFactory</c>.
/// До этого каждый экран писал свою форму руками, и разделы, до которых руки
/// не дошли, годами стояли заглушками.
/// </remarks>
public sealed partial class ProbeRunnerViewModel : ObservableObject
{
    private readonly RunnerService _runner;
    private readonly IRunStore _store;
    private readonly IAgentDirectory _agents;
    private readonly DispatcherTimer _timer;

    private ActiveRunViewModel? _current;

    private readonly IDeviceStore _devices;

    public ProbeRunnerViewModel(
        RunnerService runner,
        IProbeRegistry registry,
        IRunStore store,
        IAgentDirectory agents,
        IDeviceStore devices,
        IEnumerable<string> probeNames)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(probeNames);

        var options = new List<ProbeOption>();

        foreach (var name in probeNames)
        {
            // Падение при сборке страницы, а не молчаливый пропуск: переименованная
            // проба должна ронять тест композиции, а не тихо исчезать из списка.
            if (!registry.TryGet(name, out var probe))
            {
                throw new InvalidOperationException($"Проба «{name}» не зарегистрирована.");
            }

            options.Add(new ProbeOption(probe));
        }

        Probes = options;
        Probe = options[0];

        // Очередь сырых измерений разбирается по таймеру, как на странице задержки:
        // счётчик проб в панели операций живёт, а дёрганья на каждый сэмпл нет.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => _current?.Drain();
    }

    public IReadOnlyList<ProbeOption> Probes { get; }

    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = [];

    public ObservableCollection<string> AgentNames { get; } = [];

    /// <summary>Подсказки цели из инвентаря (И-24): сеть просканирована — подставляем.</summary>
    public ObservableCollection<TargetSuggestion> Suggestions { get; } = [];

    public ObservableCollection<SeriesRow> Series { get; } = [];

    public ObservableCollection<FactRow> Facts { get; } = [];

    [ObservableProperty]
    private ProbeOption? _probe;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private bool _save = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _status = "Готово к запуску.";

    /// <summary>Состояние операции: им управляется знак у строки статуса.</summary>
    [ObservableProperty]
    private OperationState _statusState = OperationState.None;

    [ObservableProperty]
    private string? _verdictLine;

    /// <summary>Номер сохранённого прогона: по нему его ищут в журнале.</summary>
    [ObservableProperty]
    private string? _savedRunId;

    /// <summary>В чём измерены ряды — подписью под таблицей.</summary>
    [ObservableProperty]
    private string _seriesUnit = string.Empty;

    public bool NeedsTarget => Probe?.Descriptor.RequiresTarget ?? false;

    public bool WantsAgent => Probe?.Descriptor.RequiresAgent ?? false;

    public bool HasAgents => AgentNames.Count > 0;

    public bool HasSeries => Series.Count > 0;

    public bool HasFacts => Facts.Count > 0;

    public bool CanStart => !IsRunning;

    public string TargetHint => Probe?.Descriptor.RequiresAgent == true
        ? "имя сопряжённого агента"
        : "адрес, имя или «шлюз»";

    public string ProbeAbout => Probe?.Descriptor.Description ?? string.Empty;

    partial void OnProbeChanged(ProbeOption? value)
    {
        Fields.Clear();

        foreach (var parameter in value?.Descriptor.Parameters ?? [])
        {
            Fields.Add(new ParameterFieldViewModel(parameter));
        }

        OnPropertyChanged(nameof(NeedsTarget));
        OnPropertyChanged(nameof(WantsAgent));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(ProbeAbout));

        // Имя агента из цели ping не сделать, и наоборот: смена пробы очищает цель,
        // если её вид больше не подходит. Обратная половина правила отсутствовала —
        // после «Пропускной способности» в поле оставалось имя агента, и ping уходил
        // разрешать его как имя узла: отказ выглядел проблемой сети.
        if (value?.Descriptor.RequiresAgent == true)
        {
            if (AgentNames.Count > 0 && !AgentNames.Contains(Target, StringComparer.OrdinalIgnoreCase))
            {
                Target = AgentNames[0];
            }

            return;
        }

        if (AgentNames.Contains(Target, StringComparer.OrdinalIgnoreCase))
        {
            Target = string.Empty;
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Сопряжённые агенты — подставляются в цель одним нажатием.</summary>
    public async Task LoadAgentsAsync(CancellationToken cancellationToken = default)
    {
        Suggestions.Clear();

        foreach (var suggestion in await TargetSuggestions.LoadAsync(_devices, cancellationToken).ConfigureAwait(true))
        {
            Suggestions.Add(suggestion);
        }

        try
        {
            var agents = await _agents.ListAsync(cancellationToken).ConfigureAwait(true);

            AgentNames.Clear();

            foreach (var agent in agents)
            {
                AgentNames.Add(agent.DisplayName);
            }

            OnPropertyChanged(nameof(HasAgents));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Список агентов — удобство подстановки; его отказ не мешает вводу руками.
            _ = ex;
        }
    }

    [RelayCommand]
    private void UseAgent(string name) => Target = name;

    /// <summary>Открыт ли список инвентаря под полем цели.</summary>
    [ObservableProperty]
    private bool _isPickerOpen;

    // Кнопка только открывает: закрытие — выбором или щелчком мимо (light dismiss).
    [RelayCommand]
    private void OpenPicker() => IsPickerOpen = true;

    [RelayCommand]
    private void UseSuggestion(TargetSuggestion suggestion)
    {
        Target = suggestion.Address;
        IsPickerOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Probe is not { } option)
        {
            return;
        }

        Error = null;
        VerdictLine = null;
        SavedRunId = null;
        Series.Clear();
        Facts.Clear();
        OnPropertyChanged(nameof(HasSeries));
        OnPropertyChanged(nameof(HasFacts));

        Domain.Targets.Target target;

        try
        {
            // У проб без цели она служебная — как в консоли (ProbeCommandFactory).
            target = option.Descriptor.RequiresTarget
                ? TargetInput.Parse(Target)
                : Domain.Targets.Target.Parse(option.Descriptor.Title);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Error = $"Цель «{Target}» не разобрана: {ex.Message}";

            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in Fields)
        {
            if (field.Value is { } value)
            {
                parameters[field.Parameter.Name] = value;
            }
        }

        var request = new ProbeRequest { Target = target, Parameters = parameters };

        var errors = option.Probe.Validate(request);

        if (errors.Count > 0)
        {
            Error = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}"));

            return;
        }

        if (Save)
        {
            await _store.InitializeAsync().ConfigureAwait(true);
        }

        _current = _runner.Start(
            option.Probe,
            request,
            Save,
            option.Descriptor.RequiresTarget ? target.DisplayName : option.Descriptor.Title);
        _current.Finished += OnFinished;

        IsRunning = true;
        Status = "Идёт измерение…";
        StatusState = OperationState.Running;
        _timer.Start();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _current?.Cancel();
        Status = "Останавливаю — измеренное будет сохранено.";
        StatusState = OperationState.Running;
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        _timer.Stop();

        if (sender is not ActiveRunViewModel run)
        {
            return;
        }

        run.Finished -= OnFinished;
        _current = null;
        IsRunning = false;

        if (run.Outcome is not { } outcome)
        {
            Error = run.Error ?? "Прогон не удался.";
            Status = "Прогон не удался.";
            StatusState = OperationState.Failed;

            return;
        }

        var result = outcome.Result;

        // Единица берётся у результата, а не подразумевается: у пробы скорости
        // ряды — мегабиты в секунду, и подпись «времена в миллисекундах» была
        // не отсутствием единицы, а неверной единицей.
        SeriesUnit = Units.TableCaption(result.Unit);

        // Раскладка та же, что в журнале и хранилище, — по объявленной форме.
        foreach (var series in SeriesBreakdown.Compute(run.Descriptor.Shape, result.Samples))
        {
            var stats = series.Statistics;
            var empty = stats.SampleCount == 0;

            Series.Add(new SeriesRow(
                series.Label,
                series.SentCount.ToString(CultureInfo.InvariantCulture),
                $"{series.LossPercent:0} %",
                empty ? "—" : Measured(stats.MinMs, result.Unit),
                empty ? "—" : Measured(stats.P50Ms, result.Unit),
                empty ? "—" : Measured(stats.MaxMs, result.Unit),
                empty ? "—" : Measured(stats.JitterRfc3550Ms, result.Unit)));
        }

        foreach (var fact in result.Facts)
        {
            Facts.Add(new FactRow(
                fact.Category,
                fact.Name,
                fact.Value + Units.Suffix(fact.Unit),
                fact.IsWarning));
        }

        OnPropertyChanged(nameof(HasSeries));
        OnPropertyChanged(nameof(HasFacts));

        Status = (result.WasCancelled ? "Остановлено оператором. " : "Завершено. ")
                 + $"Отправлено {result.SentCount}, получено {result.SuccessCount}, "
                 + $"потери {result.LossPercent:0.#}%.";
        StatusState = OperationState.Done;

        // Номер прогона — не украшение статуса: по нему прогон ищут в журнале,
        // и набирать его с экрана руками незачем.
        SavedRunId = outcome.RunId is { } id ? id.ToString() : null;

        if (outcome.ProfileVerdict is { } verdict)
        {
            // Суждение отдельно от чисел: пороги — мнение профиля, а не измерение.
            VerdictLine = $"По порогам профиля «{outcome.ProfileName}»: {VerdictWording.Outcome(verdict.Level)}.";
        }
    }

    /// <summary>
    /// Значение ряда без единицы: единица названа один раз подписью таблицы.
    /// </summary>
    /// <remarks>
    /// В колонке с семью числами единица у каждого превращает таблицу в частокол
    /// букв; в подписи она читается один раз и относится ко всем.
    /// </remarks>
    private static string Measured(double value, MeasurementUnit unit) => Units.Number(value, unit);
}
