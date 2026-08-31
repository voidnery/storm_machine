using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Outside;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка факта: то, что проба установила, но не измерила числом.</summary>
public sealed record InspectFactRow(string Category, string Name, string Value, bool IsWarning);

/// <summary>Факты одной категории под общим подзаголовком.</summary>
public sealed record InspectFactGroup(string Category, IReadOnlyList<InspectFactRow> Rows);

/// <summary>Строка ряда: фаза водопада или отдельный резолвер.</summary>
public sealed record InspectSeriesRow(string Label, string Median, string Loss, double BarWidth, string Share);

/// <summary>Строка того, как машину видит внешний сервер.</summary>
public sealed record OutsideMappingRow(string Server, string Seen);

/// <summary>Один инспектор в переключателе.</summary>
public sealed record InspectorOption(string ProbeName, string Title, string About)
{
    public override string ToString() => Title;
}

/// <summary>
/// Экран инспекторов: DNS, TLS и HTTP.
/// </summary>
/// <remarks>
/// Инспектор отличается от пробы задержки предметом интереса. У ping вопрос «сколько
/// миллисекунд», у инспектора — «что там на самом деле»: какие записи вернул резолвер,
/// чем подписан сертификат, что ответил сервер и на какой фазе ушло время. Поэтому
/// главное на экране не график, а две таблицы: факты и разложение по рядам.
/// <para>
/// Сюда же вынесен взгляд снаружи. Он не проба и ряда измерений не даёт, но отвечает
/// на вопрос того же рода — «что там на самом деле», только про собственное подключение,
/// а изнутри сети на него не отвечает ничто.
/// </para>
/// </remarks>
public sealed partial class InspectPageViewModel : PageViewModel, ITargetAware, IDisposable
{
    private const double BarPixels = 220;

    private readonly IProbeRegistry _registry;
    private readonly RunOrchestrator _orchestrator;
    private readonly IHighResolutionClock _clock;
    private readonly IOutsideView _outside;
    private readonly IRunStore _store;

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private InspectorOption? _inspector;

    /// <summary>Принимает цель из палитры команд.</summary>
    public void UseTarget(string target) => Target = target;

    [ObservableProperty]
    private string _target = "example.com";

    [ObservableProperty]
    private bool _dnssec = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _seriesCaption = string.Empty;

    [ObservableProperty]
    private bool _isOutsideRunning;

    [ObservableProperty]
    private string _outsideSummary = string.Empty;

    [ObservableProperty]
    private string _natSummary = string.Empty;

    [ObservableProperty]
    private string _ipv6Summary = string.Empty;

    public InspectPageViewModel(
        NavigationSection section,
        IProbeRegistry registry,
        RunOrchestrator orchestrator,
        IHighResolutionClock clock,
        IOutsideView outside,
        IRunStore store)
        : base(section)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _outside = outside ?? throw new ArgumentNullException(nameof(outside));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        Inspectors =
        [
            new("dns", "DNS", "записи, сравнение резолверов, подписи DNSSEC"),
            new("tls", "TLS", "сертификат, издатель, срок годности, версия протокола"),
            new("http", "HTTP", "код ответа, заголовки и водопад таймингов"),
        ];

        Inspector = Inspectors[0];
    }

    public IReadOnlyList<InspectorOption> Inspectors { get; }

    public ObservableCollection<InspectFactRow> Facts { get; } = [];

    /// <summary>Те же факты, разложенные по категориям для показа.</summary>
    public ObservableCollection<InspectFactGroup> FactGroups { get; } = [];

    public ObservableCollection<InspectSeriesRow> Series { get; } = [];

    public ObservableCollection<OutsideMappingRow> Mappings { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public bool HasFacts => Facts.Count > 0;

    public bool HasSeries => Series.Count > 0;

    public bool HasOutside => Mappings.Count > 0;

    public bool ShowDnssec => Inspector?.ProbeName == "dns";

    public bool CanStart => !IsRunning;

    partial void OnInspectorChanged(InspectorOption? value) => OnPropertyChanged(nameof(ShowDnssec));

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        InspectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task InspectAsync()
    {
        if (Inspector is null || !_registry.TryGet(Inspector.ProbeName, out var probe))
        {
            Error = "Инспектор не найден.";
            return;
        }

        Error = null;
        Facts.Clear();
        FactGroups.Clear();
        Series.Clear();
        Summary = string.Empty;
        OnPropertyChanged(nameof(HasFacts));
        OnPropertyChanged(nameof(HasSeries));

        IsRunning = true;
        _cts = new CancellationTokenSource();

        try
        {
            await _store.InitializeAsync(_cts.Token).ConfigureAwait(true);
            await _clock.CalibrateAsync(_cts.Token).ConfigureAwait(true);

            // Схема подставляется только инспектору HTTP: у DNS цель — имя для разрешения,
            // и «https://» перед ним сделало бы запрос бессмысленным.
            var text = Inspector.ProbeName == "http"
                       && !Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? "https://" + Target
                : Target;

            var request = new ProbeRequest
            {
                Target = StormMachine.Domain.Targets.Target.Parse(text),
                Parameters = BuildParameters(),
            };

            var errors = probe.Validate(request);

            if (errors.Count > 0)
            {
                Error = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}"));
                return;
            }

            var outcome = await _orchestrator
                .RunAsync(probe, request, new RunOptions { Save = true }, _cts.Token)
                .ConfigureAwait(true);

            Show(outcome.Result, probe.Descriptor.Shape);
        }
        catch (OperationCanceledException)
        {
            Error = "Прервано.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private Dictionary<string, object?> BuildParameters()
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        switch (Inspector?.ProbeName)
        {
            case "dns":
                parameters["count"] = 3;
                parameters["type"] = "A";
                parameters["dnssec"] = Dnssec;
                parameters["resolvers"] = "1.1.1.1,8.8.8.8,9.9.9.9,77.88.8.8,208.67.222.222";
                break;

            case "tls":
                parameters["count"] = 2;
                parameters["port"] = 443;
                break;

            default:
                parameters["count"] = 3;
                break;
        }

        return parameters;
    }

    private void Show(ProbeResult result, ProbeResultShape shape)
    {
        foreach (var fact in result.Facts)
        {
            Facts.Add(new InspectFactRow(fact.Category, fact.Name, fact.Value, fact.IsWarning));
        }

        // Разбор по группам, а не сплошным потоком: у пробы DNS в одну простыню
        // сливались записи, согласованность резолверов и подписи DNSSEC — три
        // разных вопроса, и найти среди них ответ на свой было нечем. Порядок групп
        // сохраняется тот, в котором факты выдала проба: она знает, что важнее.
        foreach (var group in Facts.GroupBy(f => f.Category, StringComparer.Ordinal))
        {
            FactGroups.Add(new InspectFactGroup(group.Key, [.. group]));
        }

        var series = SeriesBreakdown.Compute(shape, result.Samples);

        // Ряд «весь прогон» в таблицу не идёт: у водопада он был бы суммой фаз,
        // а у сравнения резолверов — смесью пяти независимых рядов. И то и другое
        // рядом с составляющими читается как ещё одна составляющая.
        var largest = series.Count == 0 ? 0 : series.Max(s => s.Statistics.SampleCount > 0 ? s.Statistics.P50Ms : 0);
        var total = series.Sum(s => s.Statistics.SampleCount > 0 ? s.Statistics.P50Ms : 0);

        var phased = shape == ProbeResultShape.PhasedTiming;

        SeriesCaption = phased
            ? "Фазы одного события: они идут подряд и складываются в него целиком."
            : shape == ProbeResultShape.ComparedSeries
                ? "Независимые ряды: они идут параллельно и не складываются — доли здесь нет."
                : string.Empty;

        foreach (var row in series)
        {
            var measured = row.Statistics.SampleCount > 0;
            var median = measured ? row.Statistics.P50Ms : 0;

            Series.Add(new InspectSeriesRow(
                row.Label,
                measured ? Milliseconds(median) : "—",
                row.LostCount > 0 ? $"{row.LostCount} из {row.SentCount}" : string.Empty,
                largest > 0 ? Math.Max(2, median / largest * BarPixels) : 0,
                phased && total > 0 ? (median / total).ToString("P0", CultureInfo.InvariantCulture) : string.Empty));
        }

        var warnings = result.Facts.Count(f => f.IsWarning);

        Summary = warnings == 0
            ? $"Отправлено {result.SentCount}, получено {result.SuccessCount}. Проба ни на что не указала."
            : $"Отправлено {result.SentCount}, получено {result.SuccessCount}. "
              + $"Проба отметила находок: {warnings} — они выделены ниже.";

        OnPropertyChanged(nameof(HasFacts));
        OnPropertyChanged(nameof(HasSeries));
    }

    /// <summary>
    /// Взгляд снаружи.
    /// </summary>
    /// <remarks>
    /// Отдельной кнопкой, а не при открытии экрана: это единственное место продукта,
    /// которое обязательно обращается к чужим серверам, и делать такое обращение
    /// самовольно нельзя.
    /// </remarks>
    [RelayCommand]
    private async Task LookOutsideAsync()
    {
        IsOutsideRunning = true;
        Mappings.Clear();
        Notes.Clear();

        try
        {
            using var cts = new CancellationTokenSource();
            var view = await _outside.LookAsync(new OutsideRequest(), cts.Token).ConfigureAwait(true);

            OutsideSummary = view.ExternalAddress is null
                ? "Внешний адрес определить не удалось — см. пояснения ниже."
                : $"Снаружи машина видна как {view.ExternalAddress}:{view.ExternalPort}"
                  + (view.HostName is { Length: > 0 } name ? $" ({name})" : string.Empty)
                  + (view.AsNumber is { } asn ? $", AS{asn} {view.AsOrganization}" : string.Empty);

            NatSummary = "Трансляция адресов: " + view.DescribeMapping();
            Ipv6Summary = view.Ipv6 is { } ipv6 ? "Готовность к IPv6: " + ipv6.Describe() : string.Empty;

            foreach (var mapping in view.Mappings)
            {
                Mappings.Add(new OutsideMappingRow(
                    mapping.Server,
                    mapping.Answered
                        ? $"видит нас как {mapping.Address}:{mapping.Port}"
                        : $"не ответил ({mapping.Failure ?? "причина неизвестна"})"));
            }

            Notes.Add(OutsideView.FilteringNotTested);

            foreach (var note in view.Notes)
            {
                Notes.Add(note);
            }

            if (view.Attribution is { Length: > 0 } attribution)
            {
                Notes.Add($"Источник данных о принадлежности: {attribution}");
            }

            OnPropertyChanged(nameof(HasOutside));
        }
        finally
        {
            IsOutsideRunning = false;
        }
    }

    public override void Deactivate() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private string Milliseconds(double ms) =>
        ms > 0 && ms < _clock.CalibrationBaselineMs
            ? $"< {_clock.CalibrationBaselineMs.ToString("0.0", CultureInfo.InvariantCulture)} мс"
            : ms < 10
                ? $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} мс"
                : $"{ms.ToString("0", CultureInfo.InvariantCulture)} мс";
}
