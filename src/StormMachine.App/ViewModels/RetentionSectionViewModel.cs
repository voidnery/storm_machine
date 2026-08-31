using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Storage;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Политика хранения — экранная форма.
/// </summary>
/// <remarks>
/// До И-24 политика жила только умолчаниями и ключами <c>storm runs purge</c>.
/// Сохранённая здесь политика действует на уборку при каждом запуске продукта;
/// «Прикинуть» отвечает на вопрос «что именно удалится», не удаляя, — уборка
/// необратима, и запускать её вслепую значило бы предлагать оператору лотерею.
/// </remarks>
public sealed partial class RetentionSectionViewModel(RetentionSettings settings, IRunStore runs)
    : ObservableObject
{
    private readonly RetentionSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    [ObservableProperty]
    private decimal? _rawDays = (decimal)RetentionPolicy.Default.RawSampleHorizon.TotalDays;

    [ObservableProperty]
    private decimal? _runDays = (decimal)RetentionPolicy.Default.RunHorizon.TotalDays;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    public static string Note =>
        "Сырые сэмплы старше горизонта удаляются, агрегаты остаются — история "
        + "и отчёты продолжают работать.";

    public static string NoteWhy =>
        "Политика применяется при каждом запуске продукта, а не только по кнопке: "
        + "уборка, о которой надо помнить, не выполняется.";

    /// <summary>Та же уборка из консоли — чипом, чтобы не набирать руками.</summary>
    public static string PurgeCommand => "storm runs purge";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var policy = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);

        RawDays = (decimal)policy.RawSampleHorizon.TotalDays;
        RunDays = (decimal)policy.RunHorizon.TotalDays;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Message = Error = null;

        try
        {
            var saved = await _settings
                .SetAsync(Days(RawDays), Days(RunDays), cancellationToken)
                .ConfigureAwait(true);

            Message = $"Сохранено: сырые сэмплы {saved.RawSampleHorizon.TotalDays:0} дн., "
                      + $"прогоны {saved.RunHorizon.TotalDays:0} дн. Действует со следующего запуска "
                      + "или сразу — кнопкой «Прибраться сейчас».";
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private Task PreviewAsync(CancellationToken cancellationToken) => CleanupAsync(dryRun: true, cancellationToken);

    [RelayCommand]
    private Task CleanupNowAsync(CancellationToken cancellationToken) => CleanupAsync(dryRun: false, cancellationToken);

    private async Task CleanupAsync(bool dryRun, CancellationToken cancellationToken)
    {
        Message = Error = null;

        try
        {
            var policy = new RetentionPolicy
            {
                RawSampleHorizon = TimeSpan.FromDays(Days(RawDays)),
                RunHorizon = TimeSpan.FromDays(Days(RunDays)),
            };

            var report = await _runs
                .ApplyRetentionAsync(policy, dryRun, cancellationToken)
                .ConfigureAwait(true);

            Message = Describe(report, dryRun);
        }
        catch (Exception ex)
        {
            Error = "Уборка не прошла: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
        }
    }

    private static string Describe(RetentionReport report, bool dryRun)
    {
        if (report.IsEmpty)
        {
            return "Удалять нечего: всё внутри горизонтов.";
        }

        var counts = $"прогонов целиком {report.RunsDeleted}, свёрнуто до агрегатов {report.RunsDownsampled}, "
                     + $"сырых сэмплов {report.SamplesDeleted.ToString("N0", CultureInfo.InvariantCulture)}";

        return dryRun
            ? $"Будет удалено: {counts}. Пока не удалено ничего."
            // Про размер файла говорится сразу: место освобождается внутри него,
            // и неизменившееся число после уборки выглядит как «не сработало» (И-19).
            : $"Удалено: {counts}. Размер файла не меняется — место переиспользуется внутри него.";
    }

    private static int Days(decimal? value) => (int)Math.Round(value ?? 0);
}
