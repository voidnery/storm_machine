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

    public AlertsPageViewModel(
        NavigationSection section,
        IMonitorStore store,
        MonitorScheduler scheduler,
        IEnumerable<IAlertChannel> channels)
        : base(section)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _channels = [.. channels ?? []];

        _scheduler.Alerted += OnAlerted;
    }

    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public ObservableCollection<ChannelRow> Channels { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

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
