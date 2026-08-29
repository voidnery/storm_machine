using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Экран задержки: непрерывный ping с живым графиком и историей прогонов.
/// </summary>
/// <remarks>
/// Первый экран, которым можно пользоваться мышью. Устроен так, чтобы показывать
/// не только цифры, но и условия измерения: через какой интерфейс и с каким порогом
/// разрешения — без этого результаты несопоставимы между запусками.
/// <para>
/// График обновляется по таймеру, а не на каждый сэмпл. При интервале 100 мс
/// и нескольких прогонах поштучные обращения к диспетчеру дают заметное дёрганье,
/// а пользы не приносят: человек не различает больше десятка обновлений в секунду.
/// </para>
/// </remarks>
public sealed partial class LatencyPageViewModel : PageViewModel, ITargetAware
{
    /// <summary>Сколько точек держим на графике. Дальше окно едет.</summary>
    private const int WindowSize = 600;

    private const double ChartRefreshHz = 10;

    private readonly RunnerService _runner;
    private readonly PresetService _presets;
    private readonly IProbeRegistry _registry;
    private readonly IRunStore _store;
    private readonly IHighResolutionClock _clock;
    private readonly INetworkEnvironment _environment;

    private readonly List<double> _values = new(WindowSize);
    private readonly List<Sample> _collected = [];
    private readonly DispatcherTimer _timer;

    private ActiveRunViewModel? _current;

    public LatencyPageViewModel(
        NavigationSection section,
        RunnerService runner,
        PresetService presets,
        IProbeRegistry registry,
        IRunStore store,
        IHighResolutionClock clock,
        INetworkEnvironment environment)
        : base(section)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0 / ChartRefreshHz),
        };

        _timer.Tick += (_, _) => PumpSamples();
    }

    // ------------------------------------------------------------------ параметры

    /// <summary>Принимает цель из палитры команд.</summary>
    public void UseTarget(string target) => TargetText = target;

    [ObservableProperty]
    private string _targetText = "gateway";

    [ObservableProperty]
    private int _intervalMs = 1000;

    [ObservableProperty]
    private int _count = 60;

    [ObservableProperty]
    private bool _continuous;

    [ObservableProperty]
    private bool _saveToJournal = true;

    /// <summary>Имя, под которым текущие параметры сохранятся в библиотеку.</summary>
    [ObservableProperty]
    private string _presetName = string.Empty;

    // ------------------------------------------------------------------ состояние

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusLine = "Готово к запуску.";

    [ObservableProperty]
    private string _measurementConditions = string.Empty;

    [ObservableProperty]
    private string? _timingWarning;

    // ------------------------------------------------------------------ показатели

    [ObservableProperty]
    private string _sent = "—";

    [ObservableProperty]
    private string _loss = "—";

    [ObservableProperty]
    private string _last = "—";

    [ObservableProperty]
    private string _median = "—";

    [ObservableProperty]
    private string _jitter = "—";

    [ObservableProperty]
    private string _pdv = "—";

    /// <summary>Последние прогоны ping из журнала.</summary>
    public ObservableCollection<RunSummary> History { get; } = [];

    /// <summary>Значения на графике. Меняются только в потоке интерфейса.</summary>
    public IReadOnlyList<double> ChartValues => _values;

    /// <summary>Порог разрешения — рисуется на графике отдельной линией.</summary>
    public double FloorMs => _clock.CalibrationBaselineMs;

    /// <summary>График обновился: представлению пора перерисоваться.</summary>
    public event EventHandler? ChartUpdated;

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        UpdateConditions();

        // Если прогон продолжается, таймер перерисовки нужно вернуть: при уходе
        // со страницы он останавливается, и без этого график замер бы навсегда,
        // хотя измерение идёт и пишется в журнал.
        if (_current is { IsFinished: false })
        {
            PumpSamples();
            _timer.Start();
        }

        await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    public override void Deactivate()
    {
        // Прогон намеренно не останавливается при уходе со страницы: он виден
        // в Run Drawer и продолжает писаться в журнал. Останавливать измерение
        // потому, что оператор переключил вкладку, — потеря данных без причины.
        _timer.Stop();
    }

    // ------------------------------------------------------------------ команды

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        ErrorMessage = null;

        if (!_registry.TryGet("ping", out var probe))
        {
            ErrorMessage = "Проба ping не зарегистрирована.";
            return;
        }

        Domain.Targets.Target parsedTarget;
        try
        {
            parsedTarget = ParseTarget(TargetText);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Цель «{TargetText}» не разобрана: {ex.Message}";
            return;
        }

        // Тот же построитель, что и у сохранения в пресет: иначе запуск и пресет
        // однажды разъедутся, и оператор будет уверен, что повторяет то же измерение.
        var request = BuildRequest(parsedTarget);

        var errors = probe.Validate(request);
        if (errors.Count > 0)
        {
            ErrorMessage = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}"));
            return;
        }

        _values.Clear();
        _collected.Clear();
        ResetMetrics();
        ChartUpdated?.Invoke(this, EventArgs.Empty);

        UpdateConditions();

        if (SaveToJournal)
        {
            await _store.InitializeAsync().ConfigureAwait(true);
        }

        _current = _runner.Start(probe, request, SaveToJournal, parsedTarget.DisplayName);
        _current.Finished += OnRunFinished;

        IsRunning = true;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        StatusLine = Continuous
            ? "Идёт непрерывное измерение. Остановить можно кнопкой или из Run Drawer."
            : $"Идёт измерение: {Count} проб с интервалом {IntervalMs} мс.";

        _timer.Start();
    }

    [RelayCommand]
    private void Stop()
    {
        _current?.Cancel();
        StatusLine = "Останавливаю — измеренное будет сохранено.";
    }

    [RelayCommand]
    private async Task LoadRunAsync(RunSummary? summary)
    {
        if (summary is null || IsRunning)
        {
            return;
        }

        var run = await _store.GetAsync(summary.Id).ConfigureAwait(true);

        if (run is null)
        {
            ErrorMessage = "Прогон не найден — возможно, удалён политикой хранения.";
            return;
        }

        _values.Clear();
        _collected.Clear();
        _collected.AddRange(run.Samples);

        foreach (var sample in run.Samples)
        {
            _values.Add(sample.IsSuccess ? sample.Value : double.NaN);
        }

        RecomputeMetrics();
        ChartUpdated?.Invoke(this, EventArgs.Empty);

        StatusLine = run.Summary.HasRawSamples
            ? $"Показан прогон от {run.Summary.StartedUtc.ToLocalTime():dd.MM HH:mm:ss}."
            : "У этого прогона сырые сэмплы удалены политикой хранения — остались только агрегаты.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync() => await LoadHistoryAsync().ConfigureAwait(true);

    /// <summary>
    /// Сохраняет текущие параметры как пресет.
    /// </summary>
    /// <remarks>
    /// Сквозной принцип §2 анализа: пресет рождается не из формы, а из измерения,
    /// которое только что оказалось полезным.
    /// </remarks>
    [RelayCommand]
    private async Task SaveAsPresetAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(PresetName))
        {
            ErrorMessage = "Укажи имя пресета.";
            return;
        }

        Domain.Targets.Target parsedTarget;
        try
        {
            parsedTarget = ParseTarget(TargetText);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Цель «{TargetText}» не разобрана: {ex.Message}";
            return;
        }

        var request = BuildRequest(parsedTarget);
        var preset = PresetService.FromRequest(PresetName.Trim(), "ping", request);

        try
        {
            var existing = await _presets.FindByNameAsync(preset.Name).ConfigureAwait(true);

            if (existing is not null)
            {
                // Совпадение по имени — тот же тест, а не второй такой же.
                preset = preset with { Id = existing.Id, CreatedUtc = existing.CreatedUtc };
            }

            var saved = await _presets.SaveAsync(preset).ConfigureAwait(true);

            StatusLine = existing is null
                ? $"Сохранено как пресет «{saved.Name}» (редакция {saved.Version}). Он в разделе «Библиотека»."
                : $"Пресет «{saved.Name}» обновлён (редакция {saved.Version}).";

            PresetName = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Пресет не сохранён: {ex.Message}";
        }
    }

    private ProbeRequest BuildRequest(Domain.Targets.Target target) => new()
    {
        Target = target,
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = Continuous ? 1_000_000 : Math.Max(1, Count),
            ["interval"] = Math.Max(1, IntervalMs),
            ["size"] = 32,
            ["timeout"] = 2000,
        },
    };

    // ------------------------------------------------------------------ внутреннее

    private void PumpSamples()
    {
        if (_current is null)
        {
            return;
        }

        var drained = _current.Drain();

        if (drained.Count == 0)
        {
            return;
        }

        foreach (var sample in drained)
        {
            _collected.Add(sample);

            // Неуспешная проба рисуется разрывом, а не нулём: ноль означал бы
            // мгновенный ответ, то есть ровно противоположное произошедшему.
            _values.Add(sample.IsSuccess ? sample.Value : double.NaN);
        }

        while (_values.Count > WindowSize)
        {
            _values.RemoveAt(0);
        }

        RecomputeMetrics();
        ChartUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void OnRunFinished(object? sender, EventArgs e)
    {
        if (_current is not null)
        {
            _current.Finished -= OnRunFinished;

            // Хвост очереди мог остаться неразобранным между тиками таймера.
            PumpSamples();
        }

        var error = _current?.Error;
        var outcome = _current?.Outcome;

        _timer.Stop();
        IsRunning = false;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        if (error is not null)
        {
            ErrorMessage = error;
            StatusLine = "Прогон завершился ошибкой.";
        }
        else if (outcome?.Result.WasCancelled == true)
        {
            StatusLine = "Прогон остановлен. Измеренное сохранено.";
        }
        else
        {
            StatusLine = "Прогон завершён.";
        }

        _current = null;

        if (SaveToJournal)
        {
            _ = LoadHistoryAsync();
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var runs = await _store
                .ListAsync(new RunQuery { Limit = 15, ProbeName = "ping" }, cancellationToken)
                .ConfigureAwait(true);

            History.Clear();

            foreach (var run in runs)
            {
                History.Add(run);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Журнал недоступен: {ex.Message}";
        }
    }

    private void UpdateConditions()
    {
        var adapter = _environment.GetPrimaryAdapter();

        var context = Application.Runs.MeasurementConditions.Build(adapter, _clock, Methodology.IcmpEcho);

        MeasurementConditions =
            $"{context.InterfaceName} · порог {context.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс · {Methodology.IcmpEcho}";

        TimingWarning = context.TimingWarning;
    }

    private void ResetMetrics()
    {
        Sent = "—";
        Loss = "—";
        Last = "—";
        Median = "—";
        Jitter = "—";
        Pdv = "—";
    }

    private void RecomputeMetrics()
    {
        if (_collected.Count == 0)
        {
            ResetMetrics();
            return;
        }

        var stats = LatencyStatistics.Compute(_collected);
        var success = _collected.Count(s => s.IsSuccess);
        var lost = _collected.Count - success;

        Sent = _collected.Count.ToString(CultureInfo.InvariantCulture);
        Loss = $"{(_collected.Count == 0 ? 0 : lost * 100.0 / _collected.Count):0.0} %";

        var lastSample = _collected[^1];
        Last = lastSample.IsSuccess ? Format(lastSample.Value) : "потеря";

        if (stats.SampleCount == 0)
        {
            Median = "—";
            Jitter = "—";
            Pdv = "—";
            return;
        }

        Median = Format(stats.P50Ms);
        Jitter = Format(stats.JitterRfc3550Ms);
        Pdv = Format(stats.PdvMs);
    }

    private static string Format(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture) + " мс";

    private static Domain.Targets.Target ParseTarget(string raw)
    {
        var trimmed = raw.Trim();

        return trimmed.Equals("gateway", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("шлюз", StringComparison.OrdinalIgnoreCase)
            ? Domain.Targets.Target.Gateway("шлюз по умолчанию")
            : Domain.Targets.Target.Parse(trimmed);
    }
}
