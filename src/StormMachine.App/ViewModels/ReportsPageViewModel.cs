using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Topology;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.App.ViewModels;

/// <summary>Шаблон в выпадающем списке.</summary>
public sealed record TemplateOption(ReportTemplate Template, string Title, string About)
{
    public override string ToString() => Title;
}

/// <summary>Срок в выпадающем списке.</summary>
public sealed record PeriodOption(string Title, TimeSpan? Span)
{
    public override string ToString() => Title;
}

/// <summary>Прогон, выбираемый галочкой.</summary>
public sealed partial class RunChoice(RunSummary summary) : ObservableObject
{
    public RunSummary Summary { get; } = summary;

    public string Title => $"{Summary.ProbeName} → {Summary.TargetDisplay}";

    public string When => Summary.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);

    public string Detail => Summary.MedianMs is { } median
        ? $"{Summary.SentCount} проб, медиана {median.ToString("0.###", CultureInfo.InvariantCulture)}"
        : $"{Summary.SentCount} проб";

    [ObservableProperty]
    private bool _isChosen;
}

/// <summary>
/// Отчёты.
/// </summary>
/// <remarks>
/// Экран собирает документ из того, что уже измерено. Единственное, чего он не делает
/// сам, — вывода: поле «заключение» заполняет человек. Продукт показывает измеренное
/// и вердикты по заданным порогам, а «сеть пригодна для эксплуатации» — утверждение,
/// за которое отвечает подписавший.
/// </remarks>
public sealed partial class ReportsPageViewModel : PageViewModel
{
    private readonly IRunStore _runs;
    private readonly IMonitorStore _monitors;
    private readonly IBaselineStore _baselines;
    private readonly IReportRenderer _renderer;
    private readonly TopologyService _topology;
    private readonly IFilePicker _files;

    public ReportsPageViewModel(
        NavigationSection section,
        IRunStore runs,
        IMonitorStore monitors,
        IBaselineStore baselines,
        IReportRenderer renderer,
        TopologyService topology,
        IFilePicker files)
        : base(section)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _files = files ?? throw new ArgumentNullException(nameof(files));

        Templates =
        [
            new(ReportTemplate.Technical, "Технический", "Каждое измерение целиком: графики, ряды, факты, условия."),
            new(ReportTemplate.Executive, "Сводка", "Итог и главные числа. Одна-две страницы для решения."),
            new(ReportTemplate.Acceptance, "Акт тестирования", "Реквизиты, схема сети, таблица проверок, место подписи."),
            new(ReportTemplate.ServiceLevel, "Доступность (SLA)", "Доступность за период, инциденты, бюджет ошибок."),
        ];

        Periods =
        [
            new("за час", TimeSpan.FromHours(1)),
            new("за сутки", TimeSpan.FromDays(1)),
            new("за неделю", TimeSpan.FromDays(7)),
            new("за месяц", TimeSpan.FromDays(30)),
            new("всё время", null),
        ];

        Template = Templates[0];
        Period = Periods[1];
    }

    public IReadOnlyList<TemplateOption> Templates { get; }

    public IReadOnlyList<PeriodOption> Periods { get; }

    public ObservableCollection<RunChoice> Runs { get; } = [];

    public ObservableCollection<Monitor> Monitors { get; } = [];

    public ObservableCollection<Baseline> Baselines { get; } = [];

    [ObservableProperty]
    private TemplateOption _template;

    [ObservableProperty]
    private PeriodOption _period;

    [ObservableProperty]
    private Monitor? _monitor;

    [ObservableProperty]
    private Baseline? _baseline;

    [ObservableProperty]
    private bool _includeTopology;

    [ObservableProperty]
    private bool _includeCharts = true;

    [ObservableProperty]
    private string? _customer;

    [ObservableProperty]
    private string? _site;

    [ObservableProperty]
    private string _author = Environment.UserName;

    [ObservableProperty]
    private string? _conclusion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsAcceptance => Template.Template == ReportTemplate.Acceptance;

    public bool IsServiceLevel => Template.Template == ReportTemplate.ServiceLevel;

    /// <summary>
    /// Предупреждение о размере документа.
    /// </summary>
    /// <remarks>
    /// Технический отчёт разворачивает каждое измерение. Сто прогонов — это сто
    /// с лишним страниц, и узнать об этом лучше до нажатия кнопки, а не открыв файл.
    /// </remarks>
    public string? SizeNotice
    {
        get
        {
            var count = Runs.Count(r => r.IsChosen);

            return Template.Template == ReportTemplate.Technical && count > 20
                ? $"Выбрано измерений: {count.ToString(CultureInfo.InvariantCulture)}. "
                  + "Технический отчёт разворачивает каждое — документ выйдет на столько же разделов. "
                  + "Для сводной таблицы есть «Сводка» и «Акт»."
                : null;
        }
    }

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    partial void OnTemplateChanged(TemplateOption value)
    {
        OnPropertyChanged(nameof(IsAcceptance));
        OnPropertyChanged(nameof(IsServiceLevel));
        OnPropertyChanged(nameof(SizeNotice));
    }

    partial void OnPeriodChanged(PeriodOption value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ErrorMessage = null;

            var from = Period.Span is { } span ? DateTimeOffset.UtcNow - span : (DateTimeOffset?)null;

            var found = await _runs
                .ListAsync(new RunQuery { Limit = 500 }, cancellationToken)
                .ConfigureAwait(true);

            Runs.Clear();

            foreach (var summary in found.Where(r => from is not { } moment || r.StartedUtc >= moment))
            {
                Runs.Add(new RunChoice(summary));
            }

            // Выбраны по умолчанию все за период: обычный случай — «сделай отчёт
            // за сутки», а не «отметь галочками двадцать строк».
            foreach (var choice in Runs)
            {
                choice.IsChosen = true;
                choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SizeNotice));
            }

            Monitors.Clear();

            foreach (var monitor in await _monitors.ListAsync(cancellationToken).ConfigureAwait(true))
            {
                Monitors.Add(monitor);
            }

            Monitor ??= Monitors.FirstOrDefault();

            Baselines.Clear();

            foreach (var baseline in await _baselines
                         .ListAsync(new BaselineQuery(), cancellationToken)
                         .ConfigureAwait(true))
            {
                Baselines.Add(baseline);
            }

            OnPropertyChanged(nameof(SizeNotice));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BuildAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Message = "Собираю документ…";
        ErrorMessage = null;

        try
        {
            var chosen = new List<StoredRun>();

            foreach (var choice in Runs.Where(r => r.IsChosen))
            {
                if (await _runs.GetAsync(choice.Summary.Id, cancellationToken).ConfigureAwait(true) is { } run)
                {
                    chosen.Add(run);
                }
            }

            var request = new ReportRequest
            {
                Template = Template.Template,
                Author = Author,
                Customer = Customer,
                Site = Site,
                Conclusion = Conclusion,
                Runs = [.. chosen.OrderBy(r => r.Summary.StartedUtc)],
                Topology = IncludeTopology
                    ? await _topology.BuildAsync(cancellationToken: cancellationToken).ConfigureAwait(true)
                    : null,
                ServiceLevel = await LevelAsync(cancellationToken).ConfigureAwait(true),
                Baselines = Compare(chosen),
                IncludeCharts = IncludeCharts,
            };

            var report = await _renderer.RenderAsync(request, cancellationToken).ConfigureAwait(true);

            var path = await _files
                .PickSaveAsync($"Куда сохранить {Template.Title.ToLowerInvariant()}", report.SuggestedFileName, "pdf")
                .ConfigureAwait(true);

            if (path is null)
            {
                Message = null;

                return;
            }

            await File.WriteAllBytesAsync(path, report.Content, cancellationToken).ConfigureAwait(true);

            Message = $"Сохранено: {path} ({report.Content.Length / 1024.0:0.0} КБ)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = null;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<ServiceLevelSection?> LevelAsync(CancellationToken cancellationToken)
    {
        if (Monitor is not { } monitor || Template.Template != ReportTemplate.ServiceLevel)
        {
            return null;
        }

        var span = Period.Span ?? monitor.Objective?.Window ?? TimeSpan.FromDays(7);
        var now = DateTimeOffset.UtcNow;
        var from = now - span;

        var checks = await _monitors
            .ListChecksAsync(
                new CheckQuery { MonitorId = monitor.Id, Since = from, Limit = 100_000 },
                cancellationToken)
            .ConfigureAwait(true);

        return new ServiceLevelSection(
            monitor,
            AvailabilityCalculator.Compute(checks, from, now, monitor.Objective),
            checks);
    }

    /// <summary>
    /// Сравнение с эталоном, если он выбран.
    /// </summary>
    /// <remarks>
    /// Сравнивается прогон той же пробы: сопоставлять эталон ping с измерением http
    /// бессмысленно, и выбрать за оператора первый попавшийся значило бы выдать
    /// бессмыслицу за вывод.
    /// </remarks>
    private List<BaselineComparison> Compare(List<StoredRun> chosen)
    {
        if (Baseline is not { } baseline || chosen.Count == 0)
        {
            return [];
        }

        var run = chosen.LastOrDefault(r =>
            string.Equals(r.Summary.ProbeName, baseline.Subject, StringComparison.OrdinalIgnoreCase));

        if (run is null)
        {
            ErrorMessage =
                $"Эталон «{baseline.Name}» снят пробой «{baseline.Subject}», "
                + "а среди выбранных измерений такой нет — сравнение пропущено.";

            return [];
        }

        return
        [
            BaselineComparer.Compare(
                baseline,
                ProbeMetrics.FromStored(run.Series, run.Facts),
                run.Context),
        ];
    }
}
