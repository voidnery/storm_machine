using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка таблицы рядов в подробностях прогона.</summary>
public sealed record SeriesRow(
    string Label,
    string Sent,
    string Loss,
    string Min,
    string Median,
    string Max,
    string Jitter);

/// <summary>Строка факта.</summary>
public sealed record FactRow(string Category, string Name, string Value, bool IsWarning);

/// <summary>
/// Журнал прогонов.
/// </summary>
/// <remarks>
/// То же, что показывает <c>storm runs</c>, только мышью. Общий у них не показ,
/// а источник: оба читают из <see cref="IRunStore"/> и раскладывают результат
/// по объявленной форме.
/// </remarks>
public sealed partial class RunsPageViewModel(
    NavigationSection section,
    IRunStore store,
    IReportRenderer reportRenderer,
    IFilePicker filePicker) : PageViewModel(section)
{
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IReportRenderer _reportRenderer = reportRenderer ?? throw new ArgumentNullException(nameof(reportRenderer));
    private readonly IFilePicker _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));

    public ObservableCollection<RunSummary> Runs { get; } = [];

    public ObservableCollection<SeriesRow> Series { get; } = [];

    public ObservableCollection<FactRow> Facts { get; } = [];

    [ObservableProperty]
    private RunSummary? _selectedRun;

    [ObservableProperty]
    private string _details = "Выбери прогон в списке слева.";

    [ObservableProperty]
    private string? _retentionNotice;

    [ObservableProperty]
    private string _usage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _message;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedRunChanged(RunSummary? value)
    {
        if (value is not null)
        {
            _ = LoadDetailsAsync(value.Id);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var runs = await _store
                .ListAsync(new RunQuery { Limit = 100 }, cancellationToken)
                .ConfigureAwait(true);

            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(run);
            }

            var (size, count, samples) = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(true);

            Usage = $"Журнал: {count} прогонов, {samples.ToString("N0", CultureInfo.InvariantCulture)} сэмплов, "
                    + $"{size / 1024.0 / 1024.0:0.00} МБ · {_store.Location}";

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Журнал недоступен: {ex.Message}";
        }
    }

    /// <summary>
    /// Формирует отчёт по выбранному прогону.
    /// </summary>
    /// <remarks>
    /// Третья часть сквозной триады: «в пресет», «в расписание», «в отчёт».
    /// Движок PDF спрятан за <see cref="IReportRenderer"/> — страница о нём не знает
    /// и знать не должна.
    /// </remarks>
    [RelayCommand]
    private async Task SaveReportAsync()
    {
        Message = null;
        ErrorMessage = null;

        if (SelectedRun is null)
        {
            return;
        }

        var run = await _store.GetAsync(SelectedRun.Id).ConfigureAwait(true);

        if (run is null)
        {
            ErrorMessage = "Прогон не найден — возможно, удалён политикой хранения.";
            return;
        }

        try
        {
            var report = await _reportRenderer
                .RenderAsync(new ReportRequest { Run = run, Author = Environment.UserName })
                .ConfigureAwait(true);

            var path = await _filePicker
                .PickSaveAsync($"Куда сохранить отчёт {_reportRenderer.Format}", report.SuggestedFileName, report.FileExtension)
                .ConfigureAwait(true);

            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, report.Content).ConfigureAwait(true);

            Message = $"Отчёт сохранён: {path} ({report.Content.Length / 1024.0:0.0} КБ)";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Отчёт не сформирован: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        await _store.DeleteAsync(SelectedRun.Id).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);

        Series.Clear();
        Facts.Clear();
        Details = "Прогон удалён.";
    }

    private async Task LoadDetailsAsync(Guid id)
    {
        Series.Clear();
        Facts.Clear();
        RetentionNotice = null;

        var run = await _store.GetAsync(id).ConfigureAwait(true);

        if (run is null)
        {
            Details = "Прогон не найден.";
            return;
        }

        var summary = run.Summary;

        Details =
            $"{summary.ProbeName} → {summary.TargetDisplay}"
            + (summary.ResolvedAddress is { } resolved ? $"  ({resolved})" : string.Empty)
            + $"\n{summary.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}"
            + (summary.Duration is { } duration ? $", длился {duration.TotalSeconds:0.0} с" : string.Empty)
            + $"\nИнтерфейс: {run.Context.InterfaceName} · порог {run.Context.CalibrationBaselineMs:0.000} мс"
            + $"\nМетодика: {run.Context.Methodology}"
            + $"\nОтправлено {summary.SentCount}, получено {summary.SuccessCount}, потеряно {summary.LostCount}";

        foreach (var series in run.Series)
        {
            var stats = series.Statistics;
            var empty = stats.SampleCount == 0;

            Series.Add(new SeriesRow(
                series.Label,
                series.SentCount.ToString(CultureInfo.InvariantCulture),
                $"{series.LossPercent:0} %",
                empty ? "—" : F(stats.MinMs),
                empty ? "—" : F(stats.P50Ms),
                empty ? "—" : F(stats.MaxMs),
                empty ? "—" : F(stats.JitterRfc3550Ms)));
        }

        foreach (var fact in run.Facts)
        {
            Facts.Add(new FactRow(fact.Category, fact.Name, fact.Value, fact.IsWarning));
        }

        if (!summary.HasRawSamples)
        {
            // «Подробности состарились» и «измерений не было» выглядели бы одинаково,
            // если об этом не сказать прямо.
            RetentionNotice = "Сырые сэмплы удалены политикой хранения. Агрегаты сохранены полностью.";
        }
    }

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}
