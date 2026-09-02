using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

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
    IFilePicker filePicker,
    IRunExporter exporter,
    IBaselineStore baselines) : PageViewModel(section)
{
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IReportRenderer _reportRenderer = reportRenderer ?? throw new ArgumentNullException(nameof(reportRenderer));
    private readonly IFilePicker _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
    private readonly IRunExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly IBaselineStore _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));

    public ObservableCollection<RunSummary> Runs { get; } = [];

    public ObservableCollection<SeriesRow> Series { get; } = [];

    public ObservableCollection<FactRow> Facts { get; } = [];

    [ObservableProperty]
    private RunSummary? _selectedRun;

    [ObservableProperty]
    private string _details = "Здесь будут ряды, факты и условия выбранного прогона.";

    [ObservableProperty]
    private string? _retentionNotice;

    // Сводка журнала плитками, а не предложением: чтобы узнать одно число,
    // приходилось прочитать все четыре, а путь в конце строки обрезался.

    [ObservableProperty]
    private string _runCountText = "—";

    [ObservableProperty]
    private string _sampleCountText = "—";

    [ObservableProperty]
    private string _sizeText = "—";

    /// <summary>
    /// Свободное место внутри файла.
    /// </summary>
    /// <remarks>
    /// Говорится отдельно и только когда его заметно: после уборки размер файла
    /// не меняется, и без этой строки уборка выглядит не сработавшей.
    /// </remarks>
    [ObservableProperty]
    private string? _freeSpaceNotice;

    /// <summary>Путь к файлу базы: первый вопрос, когда журнал выглядит не так.</summary>
    public string StorePath => _store.Location;

    /// <summary>Отказ самой сводки — не отказ журнала: список уже загружен и виден.</summary>
    [ObservableProperty]
    private string? _usageError;

    /// <summary>В чём измерены ряды выбранного прогона.</summary>
    [ObservableProperty]
    private string _seriesUnit = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _message;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Есть ли выбранный прогон: кнопки над ним включаются по этому.</summary>
    public bool HasSelection => SelectedRun is not null;

    /// <summary>
    /// Что стоит вместо подробностей, пока прогон не выбран.
    /// </summary>
    /// <remarks>
    /// «Выбери прогон в списке слева» на пустом журнале — совет, которому нельзя
    /// последовать: слева ничего нет. Что делать, там уже написано, и повторять
    /// это второй раз незачем — панель говорит о себе.
    /// </remarks>
    private string NothingChosen => Runs.Count == 0
        ? "Здесь будут ряды, факты и условия выбранного прогона."
        : "Выбери прогон в списке слева.";

    partial void OnSelectedRunChanged(RunSummary? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        if (value is null)
        {
            // Подробности снятого выбора не остаются на экране: после «Обновить»
            // справа висели ряды и факты прогона, который уже не выбран.
            Details = NothingChosen;
            RetentionNotice = null;
            SeriesUnit = string.Empty;
            Series.Clear();
            Facts.Clear();

            return;
        }

        _ = LoadDetailsAsync(value.Id);
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

            if (SelectedRun is null)
            {
                // Список мог опустеть, а присвоение null поверх null уведомления не даёт.
                Details = NothingChosen;
            }

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Журнал не прочитался: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
            return;
        }

        // Сводка считается отдельно от списка: её отказ — это отказ сводки.
        // На повреждённой базе счётчик сырых измерений падал и объявлял недоступным журнал,
        // который был загружен и виден прямо под этой надписью (И-24).
        try
        {
            var usage = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(true);

            RunCountText = usage.RunCount.ToString("N0", CultureInfo.InvariantCulture);
            SampleCountText = usage.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
            SizeText = (usage.SizeBytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " МБ";

            FreeSpaceNotice = usage.HasNotableFreeSpace
                ? $"Внутри файла свободно {usage.ReusableBytes / 1024.0 / 1024.0:0.00} МБ — уйдёт под новые записи. Размер файла после уборки не уменьшается."
                : null;

            UsageError = null;
        }
        catch (Exception ex)
        {
            UsageError = "Сводка не посчиталась: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
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
    /// <summary>
    /// Выгрузка выбранного прогона.
    /// </summary>
    /// <remarks>
    /// Отдельно от отчёта: отчёт объясняет, выгрузка отдаёт. В CSV и JSON всегда
    /// попадают условия измерения — ряд чисел без интерфейса, методики и порога
    /// достоверности нельзя ни повторить, ни сопоставить.
    /// </remarks>
    [RelayCommand]
    private async Task ExportAsync(string? format)
    {
        if (SelectedRun is null)
        {
            return;
        }

        var chosen = format?.ToLowerInvariant() switch
        {
            "json" => ExportFormat.Json,
            "png" => ExportFormat.Png,
            _ => ExportFormat.Csv,
        };

        Message = null;
        ErrorMessage = null;

        try
        {
            var run = await _store.GetAsync(SelectedRun.Id).ConfigureAwait(true);

            if (run is null)
            {
                ErrorMessage = "Прогон не найден — возможно, его удалила политика хранения.";

                return;
            }

            var file = await _exporter.ExportAsync(run, chosen).ConfigureAwait(true);

            var path = await _filePicker
                .PickSaveAsync($"Куда выгрузить {file.FileExtension.ToUpperInvariant()}", file.SuggestedFileName, file.FileExtension)
                .ConfigureAwait(true);

            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, file.Content).ConfigureAwait(true);

            Message = $"Выгружено: {path}"
                      + (chosen == ExportFormat.Csv && !run.Summary.HasRawSamples
                          ? ". Сырые измерения удалены политикой хранения — выгружены сводки."
                          : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Фиксирует эталон по выбранному прогону.
    /// </summary>
    /// <remarks>
    /// Вместе с числами запоминаются условия измерения: сравнение с эталоном, снятым
    /// в других условиях, даёт красивые цифры, которых не было, и продукт обязан уметь
    /// это назвать.
    /// </remarks>
    [RelayCommand]
    private async Task CaptureBaselineAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        Message = null;
        ErrorMessage = null;

        try
        {
            var run = await _store.GetAsync(SelectedRun.Id).ConfigureAwait(true);

            if (run is null)
            {
                ErrorMessage = "Прогон не найден — возможно, его удалила политика хранения.";

                return;
            }

            var metrics = ProbeMetrics.FromStored(run.Series, run.Facts)
                .Where(m => Baseline.IsComparable(m.Key))
                .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .Select(m => new BaselineMetric(m.Key, m.Value, Baseline.HigherIsBetterFor(m.Key, run.Unit)))
                .ToList();

            if (metrics.Count == 0)
            {
                ErrorMessage = "У прогона нет ни одной метрики — фиксировать нечего.";

                return;
            }

            var name = $"{run.Summary.ProbeName} → {run.Summary.TargetDisplay}, "
                       + $"{run.Summary.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";

            var existing = await _baselines.FindAsync(name).ConfigureAwait(true);

            await _baselines.SaveAsync(new Baseline
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = name,
                Subject = run.Summary.ProbeName,
                Target = run.Target,
                Context = run.Context,
                Unit = run.Unit,
                Metrics = metrics,
                RunId = run.Summary.Id,
                CapturedUtc = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);

            Message = $"Эталон «{name}» зафиксирован: метрик {metrics.Count}. "
                      + "Сравнить с ним можно на экране отчётов или командой storm baseline compare.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

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
                .RenderAsync(ReportRequest.ForRun(run, author: Environment.UserName))
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
            + $"\nИнтерфейс: {run.Context.InterfaceName} · порог часов {run.Context.CalibrationBaselineMs:0.000} мс"
            + $"\nМетодика: {run.Context.Methodology}"
            + $"\nОтправлено {summary.SentCount}, получено {summary.SuccessCount}, потеряно {summary.LostCount}";

        // Единица берётся у прогона: журнал показывает и ping в миллисекундах,
        // и скорость в мегабитах, и подписывать их одинаково нельзя.
        SeriesUnit = Units.TableCaption(run.Unit);

        foreach (var series in run.Series)
        {
            var stats = series.Statistics;
            var empty = stats.SampleCount == 0;

            Series.Add(new SeriesRow(
                series.Label,
                series.SentCount.ToString(CultureInfo.InvariantCulture),
                $"{series.LossPercent:0} %",
                empty ? "—" : F(stats.MinMs, run.Unit),
                empty ? "—" : F(stats.P50Ms, run.Unit),
                empty ? "—" : F(stats.MaxMs, run.Unit),
                empty ? "—" : F(stats.JitterRfc3550Ms, run.Unit)));
        }

        foreach (var fact in run.Facts)
        {
            Facts.Add(new FactRow(
                fact.Category,
                fact.Name,
                fact.Value + Units.Suffix(fact.Unit),
                fact.IsWarning));
        }

        if (!summary.HasRawSamples)
        {
            // «Подробности состарились» и «измерений не было» выглядели бы одинаково,
            // если об этом не сказать прямо.
            RetentionNotice = "Сырые измерения удалены политикой хранения. Сводки сохранены полностью.";
        }
    }

    /// <summary>Значение ряда без единицы: единица названа подписью таблицы.</summary>
    private static string F(double value, MeasurementUnit unit) => Units.Number(value, unit);
}
