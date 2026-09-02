using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка адаптеров на дашборде.</summary>
public sealed record AdapterRow(string Name, string Kind, string Address, bool IsPrimary, bool IsSuspect);

/// <summary>
/// Дашборд: состояние окружения и последние прогоны.
/// </summary>
/// <remarks>
/// В И-4 показывает то, что уже есть: сетевое окружение, порог разрешения таймера
/// и журнал. Мониторы и алерты придут в И-14 — и до тех пор раздел честно об этом говорит,
/// а не изображает пустые панели.
/// </remarks>
public sealed partial class DashboardPageViewModel(
    NavigationSection section,
    INetworkEnvironment environment,
    IHighResolutionClock clock,
    IRunStore store,
    IDeviceStore devices) : PageViewModel(section)
{
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDeviceStore _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    public ObservableCollection<AdapterRow> Adapters { get; } = [];

    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    [ObservableProperty]
    private string _privileges = string.Empty;

    [ObservableProperty]
    private string _timerInfo = string.Empty;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string _journalInfo = string.Empty;

    // Сводка журнала плитками: число отдельно, совет отдельно.

    [ObservableProperty]
    private bool _hasJournal;

    [ObservableProperty]
    private string _runCountText = "—";

    [ObservableProperty]
    private string _sampleCountText = "—";

    [ObservableProperty]
    private string _sizeText = "—";

    /// <summary>
    /// Первый запуск: инвентарь пуст и оператору некуда смотреть.
    /// </summary>
    /// <remarks>
    /// Требование итерации И-8: путь от «запустил» до «вижу свою сеть» не должен
    /// требовать чтения документации. Поэтому на пустом инвентаре дашборд не показывает
    /// пустые панели, а прямо предлагает единственное осмысленное первое действие.
    /// </remarks>
    [ObservableProperty]
    private bool _isFirstRun;

    [ObservableProperty]
    private string _firstRunHint = string.Empty;

    [ObservableProperty]
    private string _inventoryInfo = string.Empty;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        Privileges = _environment.IsElevated
            ? "Права администратора: есть"
            : "Права администратора: нет — уровню 0 они и не требуются";

        // «Порог часов 0.000 мс» читается как измеренный ноль, хотя означает, что
        // калибровки ещё не было: она идёт перед первым измерением. Строка состояния
        // об этом говорила прямо, дашборд — врал числом.
        TimerInfo =
            $"Таймер: разрешение {_clock.ResolutionNanoseconds:0.###} нс, "
            + (_clock.CalibrationBaselineMs > 0
                ? $"порог часов {_clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс"
                : "порог часов ещё не измерен — калибровка идёт перед первым измерением");

        Adapters.Clear();
        var primary = _environment.GetPrimaryAdapter();

        foreach (var adapter in _environment.GetAdapters().Where(a => a.IsUp && a.IPv4Address is not null))
        {
            var suspect = AdapterWording.IsUntrustworthy(adapter.Kind);

            Adapters.Add(new AdapterRow(
                adapter.Name,
                AdapterWording.Kind(adapter.Kind),
                adapter.SubnetCidr ?? adapter.IPv4Address ?? "—",
                primary is not null && primary.Id == adapter.Id,
                suspect));
        }

        Warning = primary is null
            ? "Активный адаптер не определён — измерения будут без указания интерфейса."
            : AdapterWording.IsUntrustworthy(primary.Kind)
                ? "Измерение пойдёт через виртуальный коммутатор или VPN. Он вносит собственную задержку и джиттер — выбросы могут не иметь отношения к тестируемой сети."
                : null;

        await LoadJournalAsync(cancellationToken).ConfigureAwait(true);
        await LoadInventoryAsync(primary, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Сколько устройств известно и что предложить, если ни одного.</summary>
    private async Task LoadInventoryAsync(NetworkAdapter? primary, CancellationToken cancellationToken)
    {
        try
        {
            await _devices.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var known = await _devices.ListDevicesAsync(cancellationToken).ConfigureAwait(true);

            IsFirstRun = known.Count == 0;
            InventoryInfo = known.Count == 0
                ? "Инвентарь пуст."
                : $"В инвентаре {known.Count} устройств, отвечали в последний раз "
                  + $"{known.Count(d => d.IsOnline)}.";

            FirstRunHint = primary?.SubnetCidr is { } subnet
                ? $"Похоже, это первый запуск. Начните с того, что покажет вашу сеть целиком: "
                  + $"сканирование подсети {subnet}. Оно займёт несколько секунд, не требует прав "
                  + "администратора и найдёт даже те узлы, что молчат на ping."
                : "Похоже, это первый запуск. Начните со сканирования своей сети — "
                  + "оно займёт несколько секунд и не требует прав администратора.";
        }
        catch (Exception ex)
        {
            InventoryInfo = $"Инвентарь недоступен: {ex.Message}";
            IsFirstRun = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ActivateAsync().ConfigureAwait(true);

    private async Task LoadJournalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var runs = await _store
                .ListAsync(new RunQuery { Limit = 8 }, cancellationToken)
                .ConfigureAwait(true);

            RecentRuns.Clear();
            foreach (var run in runs)
            {
                RecentRuns.Add(run);
            }
        }
        catch (Exception ex)
        {
            JournalInfo = "Журнал не прочитался: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
            return;
        }

        // Сводка считается отдельно от списка: её отказ — это отказ сводки.
        // Однажды упавший счётчик объявил недоступным журнал, который был загружен
        // и виден на экране, — надпись врала (И-24).
        try
        {
            var usage = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(true);

            // Числа — плитками, совет — отдельной строкой: склеенные в одну серую
            // фразу, они читались как одна надпись, и ни числа, ни совета в ней
            // видно не было.
            HasJournal = usage.RunCount > 0;

            RunCountText = usage.RunCount.ToString("N0", CultureInfo.InvariantCulture);
            SampleCountText = usage.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
            SizeText = (usage.SizeBytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " МБ";

            JournalInfo = usage.RunCount == 0
                ? "Журнал пуст. Запусти измерение — прогоны сохраняются автоматически."
                : string.Empty;
        }
        catch (Exception ex)
        {
            JournalInfo = "Сводка журнала не посчиталась: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
        }
    }
}
