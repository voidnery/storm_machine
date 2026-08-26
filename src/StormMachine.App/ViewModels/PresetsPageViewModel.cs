using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Presets;
using StormMachine.Domain.Presets;

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
    IFilePicker filePicker) : PageViewModel(section)
{
    private readonly PresetService _presets = presets ?? throw new ArgumentNullException(nameof(presets));
    private readonly RunnerService _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IFilePicker _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));

    public ObservableCollection<Preset> Presets { get; } = [];

    public ObservableCollection<ParameterRow> Parameters { get; } = [];

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

    public override async Task ActivateAsync(CancellationToken cancellationToken = default) =>
        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    partial void OnSelectedChanged(Preset? value) => ShowDetails(value);

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

        if (!_presets.TryGetProbe(Selected, out var probe))
        {
            ErrorMessage = $"Проба «{Selected.ProbeName}» не зарегистрирована.";
            return;
        }

        var preset = Selected;

        _runner.Start(
            probe,
            PresetService.ToRequest(preset),
            save: true,
            title: $"{preset.Name}",
            presetId: preset.Id,
            presetVersion: preset.Version);

        await _presets.RecordRunAsync(preset.Id).ConfigureAwait(true);

        Message = $"Запущен «{preset.Name}». Ход выполнения — в панели операций, результат попадёт в журнал.";

        await RefreshAsync().ConfigureAwait(true);
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

    private void ShowDetails(Preset? preset)
    {
        Parameters.Clear();
        ValidationWarning = null;

        if (preset is null)
        {
            Details = "Выбери пресет в списке слева.";
            return;
        }

        Details =
            $"{preset.Name}"
            + (string.IsNullOrWhiteSpace(preset.Description) ? string.Empty : $"\n{preset.Description}")
            + $"\n\nПроба: {preset.ProbeName} · Цель: {preset.Target.DisplayName}"
            + $"\nРедакция {preset.Version} · запусков {preset.RunCount}"
            + (preset.LastRunUtc is { } last ? $", последний {last.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty)
            + $"\nИзменён {preset.UpdatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";

        foreach (var (key, value) in preset.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Parameters.Add(new ParameterRow(key, value ?? "—"));
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
