using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Presets;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка параметра пресета для показа.</summary>
public sealed record ParameterRow(string Name, string Value);

/// <summary>
/// Библиотека пресетов.
/// </summary>
/// <remarks>
/// Смысл пресета не в экономии набора текста, а в повторяемости: измерение, которое
/// нельзя повторить теми же параметрами, не с чем сравнивать.
/// </remarks>
public sealed partial class PresetsPageViewModel(
    NavigationSection section,
    PresetService presets,
    RunnerService runner,
    IRunStore store,
    IFilePicker filePicker,
    ScenarioRunner scenarios,
    IHighResolutionClock clock,
    INetworkEnvironment environment) : PageViewModel(section)
{
    private readonly PresetService _presets = presets ?? throw new ArgumentNullException(nameof(presets));
    private readonly RunnerService _operations = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFilePicker _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
    private readonly ScenarioRunner _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public ObservableCollection<Preset> Presets { get; } = [];

    public ObservableCollection<ParameterRow> Parameters { get; } = [];

    /// <summary>
    /// Прогоны, сделанные выбранным пресетом.
    /// </summary>
    /// <remarks>
    /// Появились после проверки И-5: короткий пресет отрабатывает за секунду, панель
    /// операций тут же пустеет, и оператору казалось, что запуск сорвался. Результат
    /// должен быть виден там же, откуда запускали.
    /// </remarks>
    public ObservableCollection<RunSummary> Runs { get; } = [];

    /// <summary>Есть ли что показывать в списке прогонов.</summary>
    /// <remarks>
    /// Отдельное свойство, потому что Avalonia не приводит число к признаку видимости:
    /// привязка к <c>Runs.Count</c> молча не сработала бы.
    /// </remarks>
    public bool HasRuns => Runs.Count > 0;

    [ObservableProperty]
    private Preset? _selected;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _details = "Выбери пресет в списке слева.";

    [ObservableProperty]
    private string? _validationWarning;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Итог последнего запуска, сделанного с этой страницы.</summary>
    [ObservableProperty]
    private string? _lastOutcome;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default) =>
        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    partial void OnSelectedChanged(Preset? value)
    {
        ShowDetails(value);
        _ = LoadRunsAsync(value);
    }

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var found = await _presets
                .ListAsync(
                    new PresetQuery { Search = string.IsNullOrWhiteSpace(Search) ? null : Search },
                    cancellationToken)
                .ConfigureAwait(true);

            var previous = Selected?.Id;

            Presets.Clear();
            foreach (var preset in found)
            {
                Presets.Add(preset);
            }

            // Выбор восстанавливается после обновления: иначе после каждого запуска
            // подробности схлопывались бы, и оператору пришлось бы искать пресет заново.
            Selected = previous is { } id
                ? Presets.FirstOrDefault(p => p.Id == id)
                : Presets.FirstOrDefault();

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Библиотека недоступна: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Selected is null)
        {
            return;
        }

        Message = null;
        ErrorMessage = null;

        var errors = _presets.Validate(Selected);
        if (errors.Count > 0)
        {
            ErrorMessage = string.Join("; ", errors.Select(e => e.Message));
            return;
        }

        // Пресет сценария идёт своим путём: у него нет пробы, зато есть цепочка
        // шагов с порогами. В список длительных операций он попадает так же —
        // это самая долгая операция продукта, и следить за ней надо с любого экрана.
        if (Selected.Kind == PresetKind.Scenario)
        {
            await RunScenarioAsync(Selected).ConfigureAwait(true);

            return;
        }

        if (!_presets.TryGetProbe(Selected, out var probe))
        {
            ErrorMessage = $"Проба «{Selected.Subject}» не зарегистрирована.";
            return;
        }

        var preset = Selected;

        var run = _operations.Start(
            probe,
            PresetService.ToRequest(preset),
            save: true,
            title: $"{preset.Name}",
            presetId: preset.Id,
            presetVersion: preset.Version);

        // Итог показывается здесь же. Прогон из пресета может занять секунду,
        // и без этого оператор видит только опустевшую панель операций.
        run.Finished += (_, _) => OnRunFinished(preset, run);

        await _presets.RecordRunAsync(preset.Id).ConfigureAwait(true);

        LastOutcome = null;
        Message = $"Запущен «{preset.Name}» — идёт измерение.";

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Запуск пресета сценария.
    /// </summary>
    /// <remarks>
    /// Цель хранится исходной строкой и разрешается заново при каждом запуске:
    /// пресет «проверить все наши сайты» обязан переживать появление девятого сайта.
    /// </remarks>
    private async Task RunScenarioAsync(Preset preset)
    {
        TargetSet set;

        try
        {
            set = TargetSets.Resolve(preset.Target.Value, _environment);
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;

            return;
        }

        if (set.Targets.Count == 0)
        {
            ErrorMessage = $"Набор «{set.Key}» пуст: {set.Origin}.";

            return;
        }

        var cts = new CancellationTokenSource();
        var operation = _operations.StartScenario(preset.Name, cts);

        Message = $"Запущен «{preset.Name}» — проверяю {set.Targets.Count} "
                  + (set.Targets.Count == 1 ? "цель." : "целей.");

        await _presets.RecordRunAsync(preset.Id).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);

        _ = ExecuteScenarioAsync(preset, set, operation, cts);
    }

    private async Task ExecuteScenarioAsync(
        Preset preset,
        TargetSet set,
        ActiveScenarioViewModel operation,
        CancellationTokenSource cts)
    {
        var failed = 0;

        try
        {
            await _clock.CalibrateAsync(cts.Token).ConfigureAwait(true);

            foreach (var target in set.Targets)
            {
                operation.SetTarget(set.Targets.Count > 1 ? target : null);

                var scenario = ScenarioTemplates.Create(preset.Subject, target);
                var run = await _scenarios
                    .RunAsync(scenario, save: true, operation.Report, cts.Token)
                    .ConfigureAwait(true);

                if (run.Level == VerdictLevel.Fail)
                {
                    failed++;
                }
            }

            Message = failed == 0
                ? $"«{preset.Name}»: все цели прошли ({set.Targets.Count})."
                : $"«{preset.Name}»: не прошло целей {failed} из {set.Targets.Count}. "
                  + "Разбор — на экране внешних проб и в журнале.";
        }
        catch (OperationCanceledException)
        {
            Message = $"«{preset.Name}» — прервано.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            operation.Finish();
            _operations.Remove(operation);
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var name = Selected.Name;
        await _presets.DeleteAsync(Selected.Id).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);

        Message = $"Пресет «{name}» удалён. Прогоны, сделанные им, остаются в журнале.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        Message = null;
        ErrorMessage = null;

        if (Presets.Count == 0)
        {
            ErrorMessage = "Выгружать нечего: библиотека пуста.";
            return;
        }

        var path = await _filePicker
            .PickSaveAsync("Куда выгрузить пресеты", "storm-presets.json", "json")
            .ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        try
        {
            var json = PresetBundleJson.Write(PresetService.ToBundle(Presets, Environment.UserName));
            await File.WriteAllTextAsync(path, json).ConfigureAwait(true);

            Message = $"Выгружено пресетов: {Presets.Count} → {path}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось выгрузить: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        Message = null;
        ErrorMessage = null;

        var path = await _filePicker
            .PickOpenAsync("Файл с пресетами", "json")
            .ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        try
        {
            var bundle = PresetBundleJson.Read(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            var report = await _presets.ImportAsync(bundle).ConfigureAwait(true);

            await RefreshAsync().ConfigureAwait(true);

            Message = $"Добавлено {report.Added}, обновлено {report.Updated}, пропущено {report.Skipped}."
                      + (report.Problems.Count > 0 ? " " + string.Join(" ", report.Problems) : string.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Файл не прочитан: {ex.Message}";
        }
    }

    private void OnRunFinished(Preset preset, ActiveRunViewModel run)
    {
        if (run.Error is { } error)
        {
            LastOutcome = null;
            ErrorMessage = $"«{preset.Name}»: {error}";
            return;
        }

        if (run.Outcome?.Result is not { } result)
        {
            return;
        }

        var stats = Domain.Measurements.LatencyStatistics.Compute(result.Samples);
        var median = stats.SampleCount == 0
            ? "—"
            : stats.P50Ms.ToString("0.000", CultureInfo.InvariantCulture) + " мс";

        LastOutcome =
            $"«{preset.Name}»: отправлено {result.SentCount}, потери {result.LossPercent:0.0} %, медиана {median}"
            + (result.WasCancelled ? " (прогон остановлен)" : string.Empty);

        Message = null;

        _ = LoadRunsAsync(preset);
    }

    private async Task LoadRunsAsync(Preset? preset)
    {
        Runs.Clear();
        OnPropertyChanged(nameof(HasRuns));

        if (preset is null)
        {
            return;
        }

        try
        {
            var runs = await _store
                .ListAsync(new RunQuery { Limit = 10, PresetId = preset.Id })
                .ConfigureAwait(true);

            foreach (var run in runs)
            {
                Runs.Add(run);
            }

            OnPropertyChanged(nameof(HasRuns));
        }
        catch (Exception ex)
        {
            ErrorMessage = "История прогонов не прочиталась: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
        }
    }

    private void ShowDetails(Preset? preset)
    {
        Parameters.Clear();
        ValidationWarning = null;

        if (preset is null)
        {
            Details = "Выбери пресет в списке слева.";
            return;
        }

        // Имя пробы человеческое, а не ключ из базы: «ICMP Echo», а не «ping».
        var probe = _presets.TryGetProbe(preset, out var found) ? found.Descriptor : null;

        Details =
            $"{preset.Name}"
            + (string.IsNullOrWhiteSpace(preset.Description) ? string.Empty : $"\n{preset.Description}")
            + $"\n\nПроба: {probe?.Title ?? preset.Subject} · Цель: {preset.Target.DisplayName}"
            + $"\nРедакция {preset.Version} · запусков {preset.RunCount}"
            + (preset.LastRunUtc is { } last ? $", последний {last.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty)
            + $"\nИзменён {preset.UpdatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";

        // Параметры подписываются так же, как в форме запуска: у пробы для каждого
        // объявлена человеческая подпись с единицей («Интервал, мс»), и показывать
        // вместо неё ключ «interval» значило бы заставить оператора помнить оба.
        foreach (var (key, value) in preset.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var label = probe?.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Label;

            Parameters.Add(new ParameterRow(label ?? key, value ?? "—"));
        }

        // Пресет мог быть создан, когда параметры пробы были другими. Узнать об этом
        // лучше до запуска, а не по непонятной ошибке во время него.
        var errors = _presets.Validate(preset);
        if (errors.Count > 0)
        {
            ValidationWarning = "Пресет не пройдёт проверку при запуске: "
                                + string.Join("; ", errors.Select(e => e.Message));
        }
    }
}
