using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Presets;
using StormMachine.Domain.Presets;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Сохранение настроенного измерения в пресет — одинаково на всех страницах.
/// </summary>
/// <remarks>
/// Пресет рождается не из формы, а из измерения, которое только что оказалось
/// полезным (сквозной принцип §2 анализа). Отсюда и место кнопки: рядом с «Запустить»,
/// а не в библиотеке — в библиотеке нет ни пробы, ни цели, ни параметров.
/// <para>
/// Раньше это было написано четыре раза: на «Задержке», на «Анализе пути», в общем
/// прогонщике и нигде для сценариев. Копии успели разойтись — три сообщали об успехе
/// по-разному, а перезапись одноимённого была не везде. Здесь одна механика, а страница
/// приносит только то, что знает сама: как собрать пресет из своей формы.
/// </para>
/// </remarks>
public sealed partial class PresetSaveViewModel : ObservableObject
{
    private readonly PresetService _presets;
    private readonly Func<string, Preset> _build;
    private readonly Func<string?>? _suggest;

    /// <param name="build">
    /// Как собрать пресет из формы страницы. Может бросить — неразобранная цель
    /// объясняется здесь же, а не роняет команду.
    /// </param>
    /// <param name="suggest">Имя по умолчанию: «Ping (ICMP Echo) → 192.168.1.1».</param>
    public PresetSaveViewModel(PresetService presets, Func<string, Preset> build, Func<string?>? suggest = null)
    {
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _suggest = suggest;
    }

    /// <summary>Уже сохранённые — чтобы перезаписать, а не завести второй такой же.</summary>
    public ObservableCollection<Preset> Saved { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _message;

    [RelayCommand]
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = !IsOpen;

        if (!IsOpen)
        {
            return;
        }

        Message = null;

        if (string.IsNullOrWhiteSpace(Name) && _suggest?.Invoke() is { Length: > 0 } suggested)
        {
            Name = suggested;
        }

        try
        {
            var saved = await _presets
                .ListAsync(new PresetQuery { Limit = 100 }, cancellationToken)
                .ConfigureAwait(true);

            Saved.Clear();

            foreach (var preset in saved)
            {
                Saved.Add(preset);
            }
        }
        catch (Exception ex)
        {
            // Список — удобство перезаписи; без него сохранить по имени по-прежнему можно.
            Message = Trouble.Say(ex);
        }
    }

    /// <summary>Подставляет имя существующего: значит, перезаписать его.</summary>
    [RelayCommand]
    private void Use(Preset? preset)
    {
        if (preset is not null)
        {
            Name = preset.Name;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Message = "Назовите пресет — по имени его и ищут.";

            return;
        }

        try
        {
            var preset = _build(Name.Trim());

            // Совпадение по имени — тот же тест, а не второй такой же: два пресета
            // с одним именем невозможно выбрать осмысленно ни здесь, ни в мониторе.
            var existing = await _presets.FindByNameAsync(preset.Name, cancellationToken).ConfigureAwait(true);

            if (existing is not null)
            {
                preset = preset with { Id = existing.Id, CreatedUtc = existing.CreatedUtc };
            }

            var stored = await _presets.SaveAsync(preset, cancellationToken).ConfigureAwait(true);

            Message = existing is null
                ? $"Сохранён как «{stored.Name}» — он в «Библиотеке»."
                : $"Пресет «{stored.Name}» перезаписан (редакция {stored.Version}).";

            IsOpen = false;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = Trouble.Say(ex);
        }
    }
}
