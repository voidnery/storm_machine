using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Profiles;
using StormMachine.Domain.Profiles;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Перенос настроек между машинами — экранная форма.
/// </summary>
/// <remarks>
/// Механизм готов с И-22 (сценарии добавлены в И-23), но жил только в консоли:
/// «я настроил у себя, разворачиваю у заказчика» — сценарий и того, кто поставил
/// графический клиент. Долг закрыт в И-24 по решению оператора.
/// </remarks>
public sealed partial class TransferSectionViewModel(SettingsTransfer transfer, IFilePicker picker)
    : ObservableObject
{
    private readonly SettingsTransfer _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
    private readonly IFilePicker _picker = picker ?? throw new ArgumentNullException(nameof(picker));

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    /// <summary>Пропускать то, что уже есть, вместо обновления — как --keep в консоли.</summary>
    [ObservableProperty]
    private bool _keepExisting;

    public static string Note =>
        "Переносятся профили окружения, мониторы, эталоны и сценарии. Пресеты переносятся "
        + "со страницы «Библиотека». " + SettingsTransfer.SecretsNote;

    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        Message = Error = null;

        try
        {
            var bundle = await _transfer.ExportAsync(cancellationToken).ConfigureAwait(true);

            if (bundle.IsEmpty)
            {
                Message = "Переносить нечего: ни профилей, ни мониторов, ни эталонов, ни сценариев.";

                return;
            }

            var path = await _picker
                .PickSaveAsync("Выгрузка настроек", "storm-настройки", "json")
                .ConfigureAwait(true);

            if (path is null)
            {
                return;
            }

            await File.WriteAllTextAsync(path, SettingsTransfer.Write(bundle), cancellationToken)
                .ConfigureAwait(true);

            Message = $"Выгружено в {path}: {bundle.Describe()}.";
        }
        catch (Exception ex)
        {
            Error = "Выгрузка не удалась: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        Message = Error = null;

        try
        {
            var path = await _picker
                .PickOpenAsync("Загрузка настроек", "json")
                .ConfigureAwait(true);

            if (path is null)
            {
                return;
            }

            SettingsBundle bundle;

            try
            {
                bundle = SettingsTransfer.Read(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                Error = "Файл не разобран: " + ex.Message;

                return;
            }

            var report = await _transfer
                .ImportAsync(bundle, overwrite: !KeepExisting, cancellationToken)
                .ConfigureAwait(true);

            Message = $"Добавлено {report.Added}, обновлено {report.Updated}, пропущено {report.Skipped}."
                      + (report.Added + report.Updated > 0 && bundle.Profiles.Count > 0
                          // Профиль приезжает неактивным всегда: смена профиля меняет пороги
                          // и состав мониторов, а делать это молча значит поменять смысл
                          // измерений за спиной оператора. Совет только когда профили
                          // в файле были — иначе он сбивает (стенд И-24).
                          ? " Профили приехали неактивными — активируйте нужный в списке выше."
                          : string.Empty);

            if (report.Problems.Count > 0)
            {
                Error = "Не перенеслось: " + string.Join("; ", report.Problems);
            }
        }
        catch (Exception ex)
        {
            Error = "Загрузка не удалась: " + ex.Message;
        }
    }
}
