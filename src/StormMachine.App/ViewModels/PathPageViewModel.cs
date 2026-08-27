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
/// Экран анализа пути: traceroute и непрерывный MTR.
/// </summary>
/// <remarks>
/// Разовая трассировка отвечает на вопрос «каким путём», непрерывная — на вопрос
/// «где рвётся». Второй и есть настоящий: проблему, которая случается раз в минуту,
/// разовым запуском не поймать, а разговор с провайдером начинается именно с неё.
/// <para>
/// Таблица пересчитывается раз в секунду, а не на каждый сэмпл: цикл наблюдения идёт
/// с той же частотой, и чаще обновлять нечего.
/// </para>
/// </remarks>
public sealed partial class PathPageViewModel : PageViewModel
{
    private const double RefreshHz = 1;

    private readonly RunnerService _runner;
    private readonly PresetService _presets;
    private readonly IProbeRegistry _registry;
    private readonly IRunStore _store;
    private readonly IHighResolutionClock _clock;
    private readonly INetworkEnvironment _environment;
    private readonly IHopAnnotator _annotator;

    private readonly List<Sample> _collected = [];
    private readonly Dictionary<int, HopRowViewModel> _rows = [];
    private readonly Dictionary<string, string> _annotations = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;

    private ActiveRunViewModel? _current;
    private string? _resolvedAddress;

    public PathPageViewModel(
        NavigationSection section,
        RunnerService runner,
        PresetService presets,
        IProbeRegistry registry,
        IRunStore store,
        IHighResolutionClock clock,
        INetworkEnvironment environment,
        IHopAnnotator annotator)
        : base(section)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _annotator = annotator ?? throw new ArgumentNullException(nameof(annotator));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / RefreshHz) };
        _timer.Tick += (_, _) => PumpSamples();
    }

    // ------------------------------------------------------------------ параметры

    [ObservableProperty]
    private string _targetText = "1.1.1.1";

    [ObservableProperty]
    private int _maxHops = 30;

    [ObservableProperty]
    private int _rounds = 60;

    [ObservableProperty]
    private int _intervalMs = 1000;

    [ObservableProperty]
    private int _attempts = 3;

    /// <summary>Непрерывное наблюдение до остановки вручную.</summary>
    [ObservableProperty]
    private bool _continuous;

    [ObservableProperty]
    private bool _saveToJournal = true;

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

    /// <summary>Отсутствие базы принадлежности — не ошибка, но сказать об этом надо.</summary>
    [ObservableProperty]
    private string? _asnHint;

    // ------------------------------------------------------------------ итог

    [ObservableProperty]
    private string _verdict = "Маршрут ещё не построен.";

    [ObservableProperty]
    private string? _degradation;

    [ObservableProperty]
    private string? _routeChanges;

    /// <summary>Пояснение к цели, отвечающей с нескольких TTL.</summary>
    [ObservableProperty]
    private string? _pathLengthNote;

    [ObservableProperty]
    private string _hopCount = "—";

    [ObservableProperty]
    private string _silentHops = "—";

    /// <summary>Строки таблицы маршрута.</summary>
    public ObservableCollection<HopRowViewModel> Hops { get; } = [];

    /// <summary>Последние трассировки из журнала.</summary>
    public ObservableCollection<RunSummary> History { get; } = [];

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        UpdateConditions();

        if (_current is { IsFinished: false })
        {
            PumpSamples();
            _timer.Start();
        }

        await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    public override void Deactivate() => _timer.Stop();

    // ------------------------------------------------------------------ команды

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        ErrorMessage = null;

        if (!_registry.TryGet("trace", out var probe))
        {
            ErrorMessage = "Проба trace не зарегистрирована.";
            return;
        }

        Target parsedTarget;
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

        var errors = probe.Validate(request);
        if (errors.Count > 0)
        {
            ErrorMessage = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}"));
            return;
        }

        ResetView();
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
            ? "Идёт непрерывное наблюдение. Остановить можно кнопкой или из Run Drawer."
            : $"Идёт наблюдение: {Rounds} циклов с интервалом {IntervalMs} мс.";

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

        ResetView();
        _collected.AddRange(run.Samples);
        _resolvedAddress = run.Summary.ResolvedAddress;
        ReadAnnotations(run.Facts);

        // Сырых сэмплов может уже не быть — тогда разбор восстанавливается из агрегатов.
        Apply(run.Samples.Count > 0
            ? PathAnalysis.Compute(run.Samples, _resolvedAddress)
            : PathAnalysis.FromSeries(run.Series, _resolvedAddress));

        StatusLine = run.Summary.HasRawSamples
            ? $"Показана трассировка от {run.Summary.StartedUtc.ToLocalTime():dd.MM HH:mm:ss}."
            : "У этого прогона сырые сэмплы удалены политикой хранения — таблица собрана из агрегатов.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync() => await LoadHistoryAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task SaveAsPresetAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(PresetName))
        {
            ErrorMessage = "Укажи имя пресета.";
            return;
        }

        Target parsedTarget;
        try
        {
            parsedTarget = ParseTarget(TargetText);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Цель «{TargetText}» не разобрана: {ex.Message}";
            return;
        }

        var preset = PresetService.FromRequest(PresetName.Trim(), "trace", BuildRequest(parsedTarget));

        try
        {
            var existing = await _presets.FindByNameAsync(preset.Name).ConfigureAwait(true);

            if (existing is not null)
            {
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

    private ProbeRequest BuildRequest(Target target) => new()
    {
        Target = target,
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["max-hops"] = Math.Max(1, MaxHops),
            ["attempts"] = Math.Max(1, Attempts),
            ["rounds"] = Continuous ? 100_000 : Math.Max(1, Rounds),
            ["interval"] = Math.Max(100, IntervalMs),
            ["timeout"] = 2000,
            ["size"] = 32,
        },
    };

    // ------------------------------------------------------------------ внутреннее

    private void ResetView()
    {
        _collected.Clear();
        _rows.Clear();
        _annotations.Clear();
        Hops.Clear();

        _resolvedAddress = null;
        Verdict = "Маршрут ещё не построен.";
        Degradation = null;
        RouteChanges = null;
        PathLengthNote = null;
        HopCount = "—";
        SilentHops = "—";
    }

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

        _collected.AddRange(drained);

        // Адрес цели известен только после разрешения имени, поэтому берётся
        // из итога прогона, а до его появления — из последнего успешного хопа.
        _resolvedAddress ??= _current.Outcome?.Result.ResolvedAddress;

        Apply(PathAnalysis.Compute(_collected, _resolvedAddress));
    }

    private void Apply(PathAnalysis analysis)
    {
        foreach (var hop in analysis.Hops)
        {
            if (!_rows.TryGetValue(hop.Hop, out var row))
            {
                row = new HopRowViewModel(hop.Hop);
                _rows[hop.Hop] = row;
                Hops.Add(row);
            }

            row.Update(hop);
            row.IsDegradationPoint = analysis.DegradationPoint?.Hop == hop.Hop;
            row.Annotation = _annotations.TryGetValue(row.Address, out var text)
                             && text != HopAnnotation.PrivateLabel
                ? text
                : null;
        }

        HopCount = analysis.Hops.Count.ToString(CultureInfo.InvariantCulture);
        SilentHops = analysis.SilentHops.ToString(CultureInfo.InvariantCulture);

        Verdict = DescribeVerdict(analysis);
        Degradation = DescribeDegradation(analysis);
        RouteChanges = DescribeRouteChanges(analysis);
        PathLengthNote = DescribePathLength(analysis);
    }

    /// <summary>
    /// Объясняет цель, отвечающую с нескольких TTL.
    /// </summary>
    /// <remarks>
    /// Без пояснения такие строки читаются как «до цели девяносто процентов потерь»,
    /// хотя означают ровно обратное: часть пакетов дошла коротким путём.
    /// </remarks>
    private static string? DescribePathLength(PathAnalysis analysis) =>
        analysis.EarlyDestinationHops.Count == 0
            ? null
            : $"Цель отвечала также с хопов {string.Join(", ", analysis.EarlyDestinationHops)}: "
              + "длина пути непостоянна — обычное дело для туннелей MPLS без переноса TTL "
              + "и балансировки по каналам. Потери на этих строках — доля пакетов, ушедших "
              + "длинным путём, а не потерянных.";

    private static string DescribeVerdict(PathAnalysis analysis)
    {
        if (analysis.Hops.Count == 0)
        {
            return "Маршрут ещё не построен.";
        }

        if (!analysis.DestinationReached)
        {
            return "Цель не отвечает. Последние хопы могут фильтровать ICMP — "
                   + "это не обязательно означает недоступность.";
        }

        var voice = analysis.DestinationVoice;

        return double.IsNaN(voice.Mos)
            ? "Цель достигнута."
            : $"Цель достигнута. Качество для голоса: {voice.Grade} "
              + $"(MOS {voice.Mos.ToString("0.00", CultureInfo.InvariantCulture)}, "
              + $"R {voice.RFactor.ToString("0.0", CultureInfo.InvariantCulture)}) — "
              + "упрощённая E-модель ITU-T G.107.";
    }

    private string? DescribeDegradation(PathAnalysis analysis)
    {
        if (analysis.DegradationPoint is not { } point)
        {
            return analysis is { DestinationReached: true, Hops.Count: > 0 }
                ? "Устойчивых потерь по маршруту нет: до цели пакеты доходят."
                : null;
        }

        var address = point.Address ?? "неизвестный узел";
        var where = _annotations.TryGetValue(address, out var text) && text != HopAnnotation.PrivateLabel
            ? $"{address} ({text})"
            : address;

        return $"Деградация начинается на хопе {point.Hop}: {where}. "
               + $"Потери {point.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)} % "
               + "и держатся до конца маршрута.";
    }

    private static string? DescribeRouteChanges(PathAnalysis analysis)
    {
        const int MaxShown = 3;

        if (analysis.RouteChanges.Count == 0)
        {
            return null;
        }

        var shown = analysis.RouteChanges
            .TakeLast(MaxShown)
            .Select(c => $"хоп {c.Hop}: {c.From} → {c.To}");

        var tail = analysis.RouteChanges.Count > MaxShown ? " …" : string.Empty;

        return $"Смен маршрута: {analysis.RouteChanges.Count}. " + string.Join("; ", shown) + tail;
    }

    private void ReadAnnotations(IReadOnlyList<ProbeFact> facts)
    {
        foreach (var fact in facts)
        {
            if (string.Equals(fact.Category, HopAnnotation.FactCategory, StringComparison.OrdinalIgnoreCase))
            {
                _annotations[fact.Name] = fact.Value;
            }
        }
    }

    private void OnRunFinished(object? sender, EventArgs e)
    {
        if (_current is not null)
        {
            _current.Finished -= OnRunFinished;
            PumpSamples();
        }

        var error = _current?.Error;
        var outcome = _current?.Outcome;

        _timer.Stop();
        IsRunning = false;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        if (outcome is not null)
        {
            // Имена и принадлежность узлов проба выясняет в конце: до этого момента
            // показывать нечего, зато теперь таблица дополняется без повторного прогона.
            _resolvedAddress = outcome.Result.ResolvedAddress ?? _resolvedAddress;
            ReadAnnotations(outcome.Result.Facts);
            Apply(PathAnalysis.Compute(_collected, _resolvedAddress));
        }

        if (error is not null)
        {
            ErrorMessage = error;
            StatusLine = "Прогон завершился ошибкой.";
        }
        else if (outcome?.Result.WasCancelled == true)
        {
            StatusLine = "Наблюдение остановлено. Измеренное сохранено.";
        }
        else
        {
            StatusLine = "Наблюдение завершено.";
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
                .ListAsync(new RunQuery { Limit = 15, ProbeName = "trace" }, cancellationToken)
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

        var context = new MeasurementContext
        {
            InterfaceName = adapter?.Name ?? "неизвестен",
            AdapterKind = adapter?.Kind ?? AdapterKind.Unknown,
            InterfaceAddress = adapter?.IPv4Address,
            CalibrationBaselineMs = _clock.CalibrationBaselineMs,
            ProductVersion = ProductInfo.Version,
            Methodology = Methodology.Traceroute,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        MeasurementConditions = $"{context.InterfaceName} · порог "
                                + $"{context.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс "
                                + $"· {Methodology.Traceroute}";

        TimingWarning = context.TimingWarning;

        AsnHint = _annotator.HasAsnData
            ? null
            : "Принадлежность узлов к автономным системам не показана: база не найдена. "
              + $"Положите базу DB-IP Lite (.mmdb) в каталог {_annotator.AsnDatabaseHint} — "
              + "имена узлов работают и без неё.";
    }

    private static Target ParseTarget(string raw)
    {
        var trimmed = raw.Trim();

        return trimmed.Equals("gateway", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("шлюз", StringComparison.OrdinalIgnoreCase)
            ? Target.Gateway("шлюз по умолчанию")
            : Target.Parse(trimmed);
    }
}
