using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.App.ViewModels;

/// <summary>Что монитор запускает — выбор в форме.</summary>
public sealed record SubjectOption(bool IsScenario, string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Способ повторения — выбор в форме.</summary>
public sealed record ScheduleOption(string Title, TimeSpan? Interval, string? Cron)
{
    public override string ToString() => Title;
}

/// <summary>
/// Набранный порог и та же мысль словами.
/// </summary>
/// <remarks>
/// Набирают короткую запись, читают длинную: «p95 &lt; 50» рядом с «95-й перцентиль
/// меньше 50 мс». Без второй половины поле порогов не отвечало ни что такое p95,
/// ни в чём эти 50 (замечание оператора).
/// </remarks>
public sealed record ThresholdRow(string Text, string Explanation);

/// <summary>
/// Форма заведения монитора.
/// </summary>
/// <remarks>
/// Долг И-14: мониторы заводились только из консоли. Форма закрывает его, но не
/// пытается повторить консоль целиком — у неё другая задача. Консоль даёт полную
/// власть над расписанием и гистерезисом; форма даёт разумные умолчания и объясняет,
/// что означает каждое поле.
/// <para>
/// Выражение cron мышью не собирается: строка «0 3 * * 1-5» короче и точнее любого
/// набора выпадающих списков, а тому, кто её не знает, готовых вариантов хватает.
/// Своё выражение вводится текстом и проверяется тут же.
/// </para>
/// </remarks>
public sealed partial class MonitorEditorViewModel : ObservableObject
{
    private readonly IProbeRegistry _probes;

    public MonitorEditorViewModel(IProbeRegistry probes)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));

        Subjects =
        [
            .. _probes.Descriptors
                .Where(d => d.RequiresTarget)
                .OrderBy(d => d.Title, StringComparer.CurrentCulture)
                .Select(d => new SubjectOption(false, d.Name, $"проба: {d.Title}")),
            .. ScenarioTemplates.All.Select(t => new SubjectOption(true, t.Key, $"сценарий: {t.Title}")),
        ];

        Schedules =
        [
            new("каждые 30 секунд", TimeSpan.FromSeconds(30), null),
            new("каждую минуту", TimeSpan.FromMinutes(1), null),
            new("каждые 5 минут", TimeSpan.FromMinutes(5), null),
            new("каждые 15 минут", TimeSpan.FromMinutes(15), null),
            new("каждый час", TimeSpan.FromHours(1), null),
            new("каждый день в 3:00", null, "0 3 * * *"),
            new("по будням в 9:00", null, "0 9 * * 1-5"),
            new("своё выражение cron", null, string.Empty),
        ];

        Subject = Subjects[0];
        Schedule = Schedules[2];
    }

    public IReadOnlyList<SubjectOption> Subjects { get; }

    public IReadOnlyList<ScheduleOption> Schedules { get; }

    /// <summary>Пороги, набранные в форме.</summary>
    public ObservableCollection<ThresholdRow> Thresholds { get; } = [];

    /// <summary>Метрики, по которым бывают пороги, — с единицами и объяснением.</summary>
    public static IReadOnlyList<MetricHelp> Metrics => MetricWording.Common;

    public static string ThresholdNote =>
        "Порог — это метрика, знак сравнения и число: p95 < 50.";

    /// <summary>Формат длительности — он же у окна обслуживания и у консоли.</summary>
    public static string DurationHint =>
        "Формат: 30с, 15м, 2ч, 1д. Голое число — минуты.";

    public static string ThresholdNoteWhy =>
        "Знаки: < ≤ > ≥. Метрику берут из списка ниже, единица у каждой своя — "
        + "у времён миллисекунды, у потерь проценты. Нарушенный порог даёт монитору "
        + "отказ, а при включённом оповещении — алерт.";

    public ObservableCollection<string> Channels { get; } = [];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private SubjectOption _subject;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private ScheduleOption _schedule;

    [ObservableProperty]
    private string _cron = "0 3 * * *";

    [ObservableProperty]
    private string _thresholdText = string.Empty;

    [ObservableProperty]
    private bool _catchUp;

    [ObservableProperty]
    private bool _alert = true;

    [ObservableProperty]
    private int _raiseAfter = 2;

    [ObservableProperty]
    private int _clearAfter = 2;

    [ObservableProperty]
    private string _cooldown = "15м";

    [ObservableProperty]
    private string? _maintenance;

    [ObservableProperty]
    private double? _objectivePercent;

    [ObservableProperty]
    private string? _error;

    public bool IsCron => Schedule.Cron is not null;

    public bool NeedsCronText => Schedule.Cron == string.Empty;

    /// <summary>Что получится, сказанное словами. Пересчитывается на каждое изменение.</summary>
    public string Preview => Build(out _) is { } monitor
        ? $"{monitor.Schedule.Describe()}; "
          + (monitor.Thresholds.Count == 0
              ? "порогов нет — история будет, вердиктов не будет"
              : "пороги: " + string.Join(", ", monitor.Thresholds.Select(t => t.Describe())))
          + (monitor.Alert is { } rule ? $"; алерт — {rule.Describe()}" : "; алерт не задан")
        : Error ?? "заполните имя и цель";

    partial void OnScheduleChanged(ScheduleOption value)
    {
        OnPropertyChanged(nameof(IsCron));
        OnPropertyChanged(nameof(NeedsCronText));
        Refresh();
    }

    partial void OnNameChanged(string value) => Refresh();

    partial void OnTargetChanged(string value) => Refresh();

    partial void OnCronChanged(string value) => Refresh();

    partial void OnAlertChanged(bool value) => Refresh();

    partial void OnCatchUpChanged(bool value) => Refresh();

    partial void OnMaintenanceChanged(string? value) => Refresh();

    /// <summary>Добавляет набранный порог в список.</summary>
    [RelayCommand]
    private void AddThreshold()
    {
        var text = ThresholdText.Trim();

        if (text.Length == 0)
        {
            return;
        }

        try
        {
            // Разбирается сразу: порог, который не разобрался, лучше отвергнуть
            // при вводе, чем при сохранении, когда причина уже не на виду.
            var parsed = Threshold.Parse(text);

            Thresholds.Add(new ThresholdRow(text, MetricWording.Explain(parsed)));
            ThresholdText = string.Empty;
            Error = null;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private void RemoveThreshold(ThresholdRow? row)
    {
        if (row is not null)
        {
            Thresholds.Remove(row);
            Refresh();
        }
    }

    /// <summary>
    /// Собирает монитор из формы. Пусто — форма ещё не годится.
    /// </summary>
    /// <remarks>
    /// Одна и та же сборка используется и для предпросмотра, и для сохранения:
    /// иначе показанное в форме и записанное в базу разошлись бы, и разошлись бы
    /// незаметно.
    /// </remarks>
    public Monitor? Build(out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            problem = "Не задано имя монитора.";

            return null;
        }

        if (string.IsNullOrWhiteSpace(Target))
        {
            problem = "Не задана цель.";

            return null;
        }

        var schedule = Schedule.Interval is { } interval
            ? Domain.Monitors.Schedule.Every(interval, CatchUp ? MisfirePolicy.RunOnce : MisfirePolicy.Skip)
            : Domain.Monitors.Schedule.ByCron(
                Schedule.Cron == string.Empty ? Cron : Schedule.Cron!,
                CatchUp ? MisfirePolicy.RunOnce : MisfirePolicy.Skip);

        if (!string.IsNullOrWhiteSpace(Maintenance))
        {
            if (!MaintenanceWindow.TryParse(Maintenance, out var window) || window is null)
            {
                problem = "Окно обслуживания не разобрано. Ожидается «пн-пт 02:00-04:00 причина».";

                return null;
            }

            schedule = schedule with { Maintenance = [window] };
        }

        List<Threshold> limits;

        try
        {
            limits = [.. Thresholds.Select(t => Threshold.Parse(t.Text))];
        }
        catch (FormatException ex)
        {
            problem = ex.Message;

            return null;
        }

        var monitor = new Monitor
        {
            Id = Guid.NewGuid(),
            Name = Name.Trim(),
            Kind = Subject.IsScenario ? MonitorKind.Scenario : MonitorKind.Probe,
            Subject = Subject.Key,
            Target = Domain.Targets.Target.Parse(Target.Trim()),
            Thresholds = limits,
            Schedule = schedule,
            Alert = Alert
                ? new AlertRule
                {
                    RaiseAfter = Math.Max(1, RaiseAfter),
                    ClearAfter = Math.Max(1, ClearAfter),
                    Cooldown = Domain.Monitors.Schedule.TryParseInterval(Cooldown, out var pause)
                        ? pause
                        : TimeSpan.FromMinutes(15),
                    Channels = [.. Channels],
                }
                : null,
            Objective = ObjectivePercent is { } percent
                ? new ServiceLevelObjective { TargetPercent = percent, Window = TimeSpan.FromDays(30) }
                : null,
        };

        var errors = monitor.Validate();

        if (errors.Count > 0)
        {
            problem = string.Join("; ", errors);

            return null;
        }

        return monitor with { NextDueUtc = monitor.Schedule.NextAfter(DateTimeOffset.UtcNow) };
    }

    private void Refresh()
    {
        _ = Build(out var problem);

        Error = problem;
        OnPropertyChanged(nameof(Preview));
    }

    /// <summary>Подсказка про цель для выбранного предмета измерения.</summary>
    public string TargetHint => Subject.IsScenario
        ? "Имя узла, список через запятую или имя набора целей."
        : _probes.TryGet(Subject.Key, out var probe)
            ? probe.Descriptor.Description
            : "Адрес или имя узла.";

    partial void OnSubjectChanged(SubjectOption value)
    {
        OnPropertyChanged(nameof(TargetHint));
        Refresh();
    }

    /// <summary>Пояснение к числу проверок подряд — почему их не одна.</summary>
    public static string HysteresisHint =>
        "Одиночный выброс не поднимает алерт, одиночная удача его не снимает. "
        + "Метрика, гуляющая вокруг порога, иначе даёт поток «упало / поднялось» "
        + "каждые полминуты, после которого оповещения перестают читать.";

    public static string CronHint =>
        "Пять полей: минуты часы день-месяца месяц день-недели. "
        + "«0 3 * * *» — каждый день в три ночи. «*/5 * * * *» — каждые пять минут.";

    public string ObjectiveHint => ObjectivePercent is { } percent
        ? $"Допустимый простой при цели {percent.ToString("0.###", CultureInfo.InvariantCulture)}% — "
          + $"{Domain.Monitors.Schedule.Elapsed(TimeSpan.FromDays(30) * ((100 - percent) / 100))} за месяц."
        : "Без цели доступность считается, но ни с чем не сравнивается.";

    partial void OnObjectivePercentChanged(double? value) => OnPropertyChanged(nameof(ObjectiveHint));
}
