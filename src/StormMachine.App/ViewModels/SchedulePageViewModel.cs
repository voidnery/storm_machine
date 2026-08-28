using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Domain.Monitors;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.App.ViewModels;

/// <summary>Один предстоящий запуск.</summary>
public sealed record UpcomingRow(string When, string Relative, string Monitor, string What, bool IsSoon);

/// <summary>Окно обслуживания в сводке.</summary>
public sealed record MaintenanceRow(string Monitor, string Window);

/// <summary>
/// Расписание: что и когда запустится.
/// </summary>
/// <remarks>
/// Отдельно от списка мониторов, потому что отвечает на другой вопрос. Список говорит,
/// что происходит с каждой проверкой; расписание — что произойдёт в ближайшие часы
/// и не сойдётся ли всё в одну минуту.
/// <para>
/// Показываются <b>назначенные сроки из базы</b>, а не пересчитанные от «сейчас».
/// Разница видна ровно тогда, когда она важна: после сна машины срок остаётся в прошлом,
/// и здесь это написано словами, а не спрятано за красивой датой.
/// </para>
/// </remarks>
public sealed partial class SchedulePageViewModel(
    NavigationSection section,
    IMonitorStore store,
    MonitorScheduler scheduler) : PageViewModel(section)
{
    /// <summary>Насколько вперёд смотрит список.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(12);

    private readonly IMonitorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly MonitorScheduler _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public ObservableCollection<UpcomingRow> Upcoming { get; } = [];

    public ObservableCollection<MaintenanceRow> Maintenance { get; } = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public string SchedulerState => _scheduler.IsRunning
        ? "Планировщик работает."
        : "Планировщик остановлен — проверки по расписанию не идут.";

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ErrorMessage = null;

            var monitors = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
            var now = DateTimeOffset.UtcNow;

            Upcoming.Clear();
            Maintenance.Clear();

            var rows = new List<(DateTimeOffset At, UpcomingRow Row)>();

            foreach (var monitor in monitors.Where(m => m.IsEnabled))
            {
                foreach (var window in monitor.Schedule.Maintenance)
                {
                    Maintenance.Add(new MaintenanceRow(monitor.Name, window.Describe()));
                }

                var at = monitor.NextDueUtc;

                for (var i = 0; i < 20 && at is { } moment && moment - now < Horizon; i++)
                {
                    rows.Add((moment, Build(monitor, moment, now)));
                    at = monitor.Schedule.NextAfter(moment);
                }
            }

            foreach (var row in rows.OrderBy(r => r.At).Take(60))
            {
                Upcoming.Add(row.Row);
            }

            var enabled = monitors.Count(m => m.IsEnabled);

            Summary = enabled == 0
                ? "Включённых мониторов нет — расписанию нечего показывать."
                : $"Включённых мониторов {enabled}, запусков в ближайшие "
                  + $"{Domain.Monitors.Schedule.Elapsed(Horizon)}: {Upcoming.Count}.";

            OnPropertyChanged(nameof(SchedulerState));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private static UpcomingRow Build(Monitor monitor, DateTimeOffset at, DateTimeOffset now)
    {
        var relative = at <= now
            ? $"просрочен на {Domain.Monitors.Schedule.Elapsed(now - at)}"
            : $"через {Domain.Monitors.Schedule.Elapsed(at - now)}";

        return new UpcomingRow(
            at.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
            relative,
            monitor.Name,
            $"{monitor.Subject} · {monitor.Target.DisplayName}",
            at - now < TimeSpan.FromMinutes(5));
    }
}
