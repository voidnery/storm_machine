using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Domain.Monitors;

namespace StormMachine.App.ViewModels;

/// <summary>Событие в ленте.</summary>
public sealed record AlertRow(AlertEvent Event)
{
    public string When => Event.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public string Monitor => Event.MonitorName;

    public string Action => Event.ActionText;

    public string Reason => Event.Reason;

    public string? Summary => Event.Summary;

    public bool IsRaise => Event.Action == AlertAction.Raised;

    public bool IsClear => Event.Action == AlertAction.Cleared;

    /// <summary>
    /// Что стало с оповещением.
    /// </summary>
    /// <remarks>
    /// «Событие было, шуметь не стали» — это состояние продукта, а не пробел
    /// в истории, и читателю ленты его надо видеть.
    /// </remarks>
    public string Delivery => !Event.Notified
        ? "не оповещали: пауза между сообщениями ещё не истекла"
        : Event.Channels.Count > 0
            ? "каналы: " + string.Join(", ", Event.Channels)
            : Event.DeliveryErrors.Count > 0
                ? "ни один канал не доставил"
                : "оповещать было некуда — каналы в правиле не заданы";

    public string? Failures => Event.DeliveryErrors.Count == 0
        ? null
        : "не доставлено — " + string.Join("; ", Event.DeliveryErrors);

    public bool HasFailures => Event.DeliveryErrors.Count > 0;
}

/// <summary>Канал в списке.</summary>
public sealed record ChannelRow(string Name, string Title, bool IsConfigured, string? Missing);

/// <summary>
/// Поле настройки канала — построенное по объявлению, а не написанное руками.
/// </summary>
/// <remarks>
/// Тот же принцип, что у формы пробы: настройка объявляет себя
/// (<see cref="AlertSettings.All" />), форма строится по объявлению. Новый ключ
/// появится в окне сам, без правки разметки, — иначе окно и консоль расходятся
/// на следующем же добавлении.
/// </remarks>
public sealed partial class AlertSettingField : ObservableObject
{
    public AlertSettingField(AlertSettings.AlertSetting setting, string? current)
    {
        Setting = setting ?? throw new ArgumentNullException(nameof(setting));
        Stored = current;

        // Секрет в поле не показывается никогда — ни на экране, ни в снимке экрана,
        // ни в отчёте о поддержке. Форма говорит, задан он или нет, и заменяет
        // его целиком, если оператор вписал новое.
        Value = setting.IsSecret ? string.Empty : current ?? string.Empty;
    }

    public AlertSettings.AlertSetting Setting { get; }

    /// <summary>Что лежит в базе сейчас; для секрета — только признак «задан».</summary>
    public string? Stored { get; private set; }

    public string Title => Setting.Title;

    public string About => Setting.About;

    public bool IsSecret => Setting.IsSecret;

    public string Placeholder => Setting.IsSecret
        ? Stored is { Length: > 0 } ? "задан — впишите новый, чтобы заменить" : "не задан"
        : Setting.About;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Что писать в базу; <c>null</c> — не трогать.</summary>
    public string? ToWrite()
    {
        var text = Value.Trim();

        // Пустое поле у секрета означает «оставить как есть»: иначе открыть форму
        // и нажать «Сохранить» значило бы стереть пароль, ничего для этого не сделав.
        if (IsSecret)
        {
            return text.Length == 0 ? null : text;
        }

        return string.Equals(text, Stored ?? string.Empty, StringComparison.Ordinal) ? null : text;
    }

    /// <summary>Запоминает записанное — чтобы «Сохранить» второй раз ничего не делал.</summary>
    public void Accept(string? written)
    {
        Stored = written ?? Stored;

        if (IsSecret)
        {
            Value = string.Empty;
        }

        OnPropertyChanged(nameof(Placeholder));
    }
}

/// <summary>
/// Лента алертов и состояние каналов.
/// </summary>
/// <remarks>
/// Каналы показываются рядом с лентой не для полноты: молчащий канал опаснее
/// отсутствующего, потому что на него рассчитывают. Ненастроенный виден здесь
/// до того, как о нём вспомнят посреди аварии.
/// </remarks>
public sealed partial class AlertsPageViewModel : PageViewModel
{
    private readonly IMonitorStore _store;
    private readonly MonitorScheduler _scheduler;
    private readonly IReadOnlyList<IAlertChannel> _channels;
    private readonly ISettingsStore _settings;

    public AlertsPageViewModel(
        NavigationSection section,
        IMonitorStore store,
        MonitorScheduler scheduler,
        ISettingsStore settings,
        IEnumerable<IAlertChannel> channels)
        : base(section)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _channels = [.. channels ?? []];
    }

    /// <summary>
    /// Настройки каналов — формой, а не командой консоли.
    /// </summary>
    /// <remarks>
    /// До этого страница показывала «не настроен» и предлагала скопировать
    /// «storm alerts set». Показать неисправность и отправить чинить её в другую
    /// программу — это не помощь: оповещения настраивают там же, где видят,
    /// что они молчат.
    /// </remarks>
    public ObservableCollection<AlertSettingField> WebhookFields { get; } = [];

    public ObservableCollection<AlertSettingField> MailFields { get; } = [];

    [ObservableProperty]
    private string? _settingsMessage;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>Сохраняет изменённые поля и перечитывает состояние каналов.</summary>
    [RelayCommand]
    private async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var written = 0;

            foreach (var field in WebhookFields.Concat(MailFields))
            {
                if (field.ToWrite() is not { } value)
                {
                    continue;
                }

                // Пустая строка означает «убрать»: иначе стереть однажды заданный
                // адрес можно было бы только из консоли.
                if (value.Length == 0)
                {
                    await _settings.RemoveAsync(field.Setting.Key, cancellationToken).ConfigureAwait(true);
                    field.Accept(null);
                }
                else
                {
                    await _settings
                        .SetAsync(field.Setting.Key, value, field.IsSecret, cancellationToken)
                        .ConfigureAwait(true);
                    field.Accept(value);
                }

                written++;
            }

            SettingsMessage = written == 0
                ? "Менять нечего — значения те же."
                : $"Сохранено полей: {written}.";

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SettingsMessage = Services.Trouble.Say(ex);
        }
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        WebhookFields.Clear();
        MailFields.Clear();

        foreach (var setting in AlertSettings.All)
        {
            var current = await _settings.GetAsync(setting.Key, cancellationToken).ConfigureAwait(true);
            var field = new AlertSettingField(setting, current);

            (setting.IsMail ? MailFields : WebhookFields).Add(field);
        }
    }

    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public ObservableCollection<ChannelRow> Channels { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>
    /// Подписка на алерт заводится при каждом заходе.
    /// </summary>
    /// <remarks>
    /// Раньше она делалась один раз в конструкторе, а снималась при каждом уходе:
    /// после первого перехода на другой раздел лента переставала пополняться сама.
    /// </remarks>
    public override Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _scheduler.Alerted -= OnAlerted;
        _scheduler.Alerted += OnAlerted;

        return LoadAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(true);
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public override void Deactivate() => _scheduler.Alerted -= OnAlerted;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ErrorMessage = null;

            var alerts = await _store
                .ListAlertsAsync(new AlertQuery { Limit = 200 }, cancellationToken)
                .ConfigureAwait(true);

            Alerts.Clear();

            foreach (var alert in alerts)
            {
                Alerts.Add(new AlertRow(alert));
            }

            Channels.Clear();

            foreach (var channel in _channels)
            {
                await channel.RefreshAsync(cancellationToken).ConfigureAwait(true);

                Channels.Add(new ChannelRow(
                    channel.Name,
                    channel.Title,
                    channel.IsConfigured,
                    channel.MissingConfiguration));
            }

            var raised = alerts.Count(a => a.Action == AlertAction.Raised);
            var silent = alerts.Count(a => !a.Notified);

            Summary = alerts.Count == 0
                ? "Событий не было."
                : $"Событий {alerts.Count}, из них подъёмов {raised}"
                  + (silent > 0 ? $"; о {silent} не оповещали из-за паузы между сообщениями" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void OnAlerted(object? sender, AlertEvent alert) =>
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());
}
