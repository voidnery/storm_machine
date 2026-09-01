using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Application.Probes;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка мониторов.</summary>
public sealed record MonitorRow(Monitor Monitor, MonitorStatus Status)
{
    public string Name => Monitor.Name;

    public string Subject => $"{Monitor.Subject} · {Monitor.Target.DisplayName}";

    public string Schedule => Monitor.Schedule.Describe();

    public VerdictLevel Level => Monitor.IsEnabled ? Status.Level : VerdictLevel.Unknown;

    public string StateText => Monitor.IsEnabled
        ? VerdictWording.State(Status.Level, unknown: "ещё не проверялся")
        : "выключен";

    public bool IsAlerting => Status.Alert.IsRaised;

    public string NextText => !Monitor.IsEnabled
        ? "выключен"
        : Monitor.NextDueUtc is { } due
            ? due.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
            : "срок не назначен";
}

/// <summary>Строка проверки в истории.</summary>
public sealed record CheckRow(string When, string State, string Summary, bool IsFailure, bool IsGap);

/// <summary>
/// Мониторы.
/// </summary>
/// <remarks>
/// То же, что показывает <c>storm monitors</c>, только мышью. Общий у них не показ,
/// а источник: планировщик один на процесс, и проверка, запущенная отсюда, идёт тем же
/// путём, что и по расписанию.
/// </remarks>
public sealed partial class MonitorsPageViewModel : PageViewModel
{
    private readonly IMonitorStore _store;
    private readonly MonitorScheduler _scheduler;

    public MonitorsPageViewModel(
        NavigationSection section,
        IMonitorStore store,
        MonitorScheduler scheduler,
        IProbeRegistry probes,
        IEnumerable<IAlertChannel> channels)
        : base(section)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

        // Каналы доставки отдаются форме: монитор с включённым оповещением и пустым
        // списком каналов не оповещает никого, и узнать об этом можно было только
        // по строке в ленте алертов «оповещать было некуда».
        Editor = new MonitorEditorViewModel(
            probes ?? throw new ArgumentNullException(nameof(probes)),
            channels ?? throw new ArgumentNullException(nameof(channels)));
    }

    /// <summary>
    /// Форма заведения монитора.
    /// </summary>
    /// <remarks>
    /// Долг И-14: мониторы заводились только из консоли. Форма не повторяет консоль
    /// целиком — у неё другая задача: разумные умолчания и объяснение, что означает
    /// каждое поле.
    /// </remarks>
    public MonitorEditorViewModel Editor { get; }

    [ObservableProperty]
    private bool _isEditorOpen;

    public ObservableCollection<MonitorRow> Monitors { get; } = [];

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public bool HasChecks => Checks.Count > 0;

    [ObservableProperty]
    private MonitorRow? _selected;

    [ObservableProperty]
    private string _details = "Выбери монитор в списке слева.";

    [ObservableProperty]
    private string? _availabilityText;

    [ObservableProperty]
    private string? _coverageNotice;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public string SchedulerState => _scheduler.IsRunning
        ? $"Планировщик работает. Проверок сейчас: {_scheduler.ActiveCount}."
        : "Планировщик остановлен — проверки по расписанию не идут.";

    /// <summary>
    /// Подписка на факт проверки заводится при каждом заходе.
    /// </summary>
    /// <remarks>
    /// Раньше она делалась один раз в конструкторе, а снималась при каждом уходе:
    /// после первого же перехода на другой раздел список переставал обновляться сам
    /// и замирал до нажатия «Обновить». Двойной подписки не будет: снятие идёт
    /// перед постановкой.
    /// </remarks>
    public override Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _scheduler.Checked -= OnChecked;
        _scheduler.Checked += OnChecked;

        return RefreshAsync(cancellationToken);
    }

    public override void Deactivate() => _scheduler.Checked -= OnChecked;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ErrorMessage = null;

            var monitors = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
            var previous = Selected?.Monitor.Id;

            Monitors.Clear();

            foreach (var monitor in monitors)
            {
                var status = await _store.GetStatusAsync(monitor.Id, cancellationToken).ConfigureAwait(true);

                Monitors.Add(new MonitorRow(monitor, status));
            }

            Selected = Monitors.FirstOrDefault(m => m.Monitor.Id == previous) ?? Monitors.FirstOrDefault();

            OnPropertyChanged(nameof(SchedulerState));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Есть ли выбранный монитор: действия над ним включаются по этому.</summary>
    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(MonitorRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        if (value is null)
        {
            Details = "Выбери монитор в списке слева.";
            AvailabilityText = null;

            // Оговорка о покрытии тоже снимается: она осталась бы висеть
            // над числами, которых на экране больше нет.
            CoverageNotice = null;
            Checks.Clear();
            OnPropertyChanged(nameof(HasChecks));

            return;
        }

        Details = Describe(value);

        _ = LoadHistoryAsync(value.Monitor);
    }

    [RelayCommand]
    private void ToggleEditor() => IsEditorOpen = !IsEditorOpen;

    [RelayCommand]
    private async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        var monitor = Editor.Build(out var problem);

        if (monitor is null)
        {
            ErrorMessage = problem;

            return;
        }

        try
        {
            await _store.SaveAsync(monitor, cancellationToken).ConfigureAwait(true);

            _scheduler.Invalidate();

            Message = $"Монитор «{monitor.Name}» заведён: {monitor.Schedule.Describe()}. "
                      + "Проверки идут, пока работает клиент.";

            IsEditorOpen = false;
            Editor.Name = string.Empty;
            Editor.Target = string.Empty;
            Editor.Thresholds.Clear();

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (Selected is not { } row || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Message = $"Проверяю «{row.Name}»…";
        ErrorMessage = null;

        try
        {
            // Срок не сдвигается: проверка руками — это взгляд, а не перенос расписания.
            var check = await _scheduler.RunNowAsync(row.Monitor, cancellationToken).ConfigureAwait(true);

            Message = check.Summary;

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = null;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var enabled = !row.Monitor.IsEnabled;

        try
        {
            // Срок назначается заново от текущего момента: старый, оставшийся
            // с выключения, дал бы залп пропущенных проверок сразу после включения.
            await _store.SaveAsync(
                row.Monitor with
                {
                    IsEnabled = enabled,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    NextDueUtc = enabled ? row.Monitor.Schedule.NextAfter(DateTimeOffset.UtcNow) : null,
                },
                cancellationToken).ConfigureAwait(true);

            _scheduler.Invalidate();

            Message = $"Монитор «{row.Name}» {(enabled ? "включён" : "выключен")}.";

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(row.Monitor.Id, cancellationToken).ConfigureAwait(true);

            _scheduler.Invalidate();

            Message = $"Монитор «{row.Name}» удалён вместе с историей проверок. "
                      + "События в ленте алертов остались.";

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void OnChecked(object? sender, MonitorCheck check) =>
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    private async Task LoadHistoryAsync(Monitor monitor)
    {
        try
        {
            var window = monitor.Objective?.Window ?? TimeSpan.FromDays(1);
            var now = DateTimeOffset.UtcNow;
            var from = now - window;

            var checks = await _store
                .ListChecksAsync(new CheckQuery { MonitorId = monitor.Id, Since = from, Limit = 5000 })
                .ConfigureAwait(true);

            Checks.Clear();

            foreach (var check in checks.Take(50))
            {
                Checks.Add(new CheckRow(
                    check.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture),
                    check.Kind switch
                    {
                        CheckKind.Maintenance => "обслуживание",
                        CheckKind.Missed => "не наблюдали",
                        _ => VerdictWording.State(check.Level),
                    },
                    check.Summary,
                    check.Kind == CheckKind.Measured && check.Level == VerdictLevel.Fail,
                    check.Kind != CheckKind.Measured));
            }

            OnPropertyChanged(nameof(HasChecks));

            var availability = AvailabilityCalculator.Compute(checks, from, now, monitor.Objective);

            AvailabilityText = Describe(availability, window);

            // Оговорка про покрытие идёт отдельной строкой и остаётся видимой:
            // доступность 100% при покрытии 4% — это отсутствие данных, а не отличная сеть.
            CoverageNotice = availability.Total == 0
                ? "За период нет ни одного наблюдения — считать не из чего."
                : availability.Coverage < 0.9
                    ? $"Окно наблюдалось на {Percent(availability.Coverage * 100)} — "
                      + "числа выше предварительны."
                    : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private static string Describe(MonitorRow row)
    {
        var monitor = row.Monitor;
        var lines = new List<string>
        {
            $"{(monitor.Kind == MonitorKind.Scenario ? "Сценарий" : "Проба")} «{monitor.Subject}» "
            + $"на {monitor.Target.DisplayName}",
            $"Расписание: {monitor.Schedule.Describe()}",
            $"Пропуски: {(monitor.Schedule.Misfire == MisfirePolicy.RunOnce
                ? "после простоя выполнить один раз"
                : "после простоя пропустить")}",
        };

        foreach (var window in monitor.Schedule.Maintenance)
        {
            lines.Add($"Обслуживание: {window.Describe()}");
        }

        lines.Add(monitor.Thresholds.Count == 0
            ? "Пороги не заданы — монитор собирает историю, но ни о чём не судит."
            : "Пороги: " + string.Join(", ", monitor.Thresholds.Select(t => t.Describe())));

        lines.Add(monitor.Alert is { } rule
            ? $"Алерт: {rule.Describe()}"
              + (rule.Channels.Count > 0 ? $"; каналы: {string.Join(", ", rule.Channels)}" : "; каналы не заданы")
            : "Алерт не задан — монитор молчит.");

        if (monitor.Objective is { } objective)
        {
            lines.Add($"Цель SLA: {objective.Describe()}");
        }

        if (row.Status.Alert.IsRaised && row.Status.Alert.RaisedUtc is { } raised)
        {
            lines.Add($"Алерт поднят {raised.ToLocalTime():dd.MM HH:mm} — "
                      + $"{Domain.Monitors.Schedule.Elapsed(DateTimeOffset.UtcNow - raised)} назад.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(Availability availability, TimeSpan window)
    {
        if (availability.Total == 0)
        {
            return $"За {Domain.Monitors.Schedule.Elapsed(window)} наблюдений не было.";
        }

        var lines = new List<string>
        {
            $"Доступность {Percent(availability.UptimePercent)} "
            + $"от наблюдавшегося времени ({Domain.Monitors.Schedule.Elapsed(availability.Observed)})",
            $"Проверок {availability.Total}: норма {availability.Ok}, "
            + $"предупреждений {availability.Warn}, отказов {availability.Fail}",
            $"Простой {Domain.Monitors.Schedule.Elapsed(availability.Down)}"
            + (availability.Resolution > TimeSpan.Zero
                ? $" (± {Domain.Monitors.Schedule.Elapsed(availability.Resolution)} — "
                  + "состояние видно только в моменты проверок)"
                : string.Empty),
            $"Инцидентов {availability.Incidents.Count}",
        };

        if (availability.Maintenance > TimeSpan.Zero)
        {
            lines.Add($"Обслуживание {Domain.Monitors.Schedule.Elapsed(availability.Maintenance)} — "
                      + "исключено из расчёта");
        }

        if (availability.Unobserved > TimeSpan.Zero)
        {
            lines.Add($"Не наблюдали {Domain.Monitors.Schedule.Elapsed(availability.Unobserved)} — "
                      + "продукт не работал");
        }

        if (availability.MeanTimeToRecovery is { } mttr)
        {
            lines.Add($"Восстановление в среднем {Domain.Monitors.Schedule.Elapsed(mttr)}");
        }

        if (availability.Objective is { } objective)
        {
            var verdict = availability.IsMet switch
            {
                true => "выполняется",
                false => "НАРУШЕНА",
                _ => "оценить не по чему",
            };

            lines.Add($"Цель {objective.Describe()}: {verdict}");

            if (availability.ErrorBudget is { } budget)
            {
                lines.Add($"Бюджет ошибок {Domain.Monitors.Schedule.Elapsed(budget)}, "
                          + $"израсходовано {Percent(availability.ErrorBudgetUsedPercent ?? 0)}, "
                          + $"осталось {Domain.Monitors.Schedule.Elapsed(availability.ErrorBudgetLeft ?? TimeSpan.Zero)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Percent(double value) =>
        value.ToString(value >= 99.9 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + "%";
}
