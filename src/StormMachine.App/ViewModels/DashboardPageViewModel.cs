using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
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
    IRunStore store) : PageViewModel(section)
{
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

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

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        Privileges = _environment.IsElevated
            ? "Права администратора: есть"
            : "Права администратора: нет — уровню 0 они и не требуются";

        TimerInfo =
            $"Таймер: разрешение {_clock.ResolutionNanoseconds:0.###} нс, "
            + $"порог достоверности {_clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс";

        Adapters.Clear();
        var primary = _environment.GetPrimaryAdapter();

        foreach (var adapter in _environment.GetAdapters().Where(a => a.IsUp && a.IPv4Address is not null))
        {
            var suspect = adapter.Kind is AdapterKind.Virtual or AdapterKind.Vpn or AdapterKind.Tunnel;

            Adapters.Add(new AdapterRow(
                adapter.Name,
                DescribeKind(adapter.Kind),
                adapter.SubnetCidr ?? adapter.IPv4Address ?? "—",
                primary is not null && primary.Id == adapter.Id,
                suspect));
        }

        Warning = primary is null
            ? "Активный адаптер не определён — измерения будут без указания интерфейса."
            : primary.Kind is AdapterKind.Virtual or AdapterKind.Vpn or AdapterKind.Tunnel
                ? "Измерение пойдёт через виртуальный коммутатор или VPN. Он вносит собственную задержку и джиттер — выбросы могут не иметь отношения к тестируемой сети."
                : null;

        await LoadJournalAsync(cancellationToken).ConfigureAwait(true);
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

            var (size, count, samples) = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(true);

            JournalInfo = count == 0
                ? "Журнал пуст. Запусти измерение — прогоны сохраняются автоматически."
                : $"В журнале {count} прогонов, {samples.ToString("N0", CultureInfo.InvariantCulture)} сэмплов, {size / 1024.0 / 1024.0:0.00} МБ";
        }
        catch (Exception ex)
        {
            JournalInfo = $"Журнал недоступен: {ex.Message}";
        }
    }

    private static string DescribeKind(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "не определён",
    };
}
