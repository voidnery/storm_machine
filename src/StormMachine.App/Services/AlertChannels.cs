using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;

namespace StormMachine.App.Services;

/// <summary>Одно оповещение в ленте центра уведомлений.</summary>
public sealed record AlertNotice(string When, string Title, string Text, bool IsAlarming);

/// <summary>
/// Центр уведомлений: полоса в окне и лента того, что уже приходило.
/// </summary>
/// <remarks>
/// Отдельная служба, а не поле модели главного окна: канал не должен знать про окно,
/// а окно — про каналы. Оба знают про эту единственную точку.
/// <para>
/// Лента появилась потому, что полоса показывает <b>одно</b> оповещение и исчезает.
/// Алерт, пришедший, пока оператор смотрел в другую сторону, вытеснялся следующим
/// и пропадал бесследно: в ленте событий он оставался, но искать его там нужно было
/// зная, что он вообще был. Лента отвечает на вопрос «я что-то пропустил?» —
/// на который «Алерты» отвечать не обязаны.
/// </para>
/// </remarks>
public sealed partial class NotificationCenter : ObservableObject
{
    /// <summary>
    /// Сколько оповещений держится в ленте.
    /// </summary>
    /// <remarks>
    /// Лента — не журнал: полная история лежит в базе и показывается на экране алертов.
    /// Здесь достаточно того, что человек мог пропустить, пока его не было за столом.
    /// </remarks>
    public const int Capacity = 50;

    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private bool _isAlarming;

    [ObservableProperty]
    private int _unreadCount;

    public bool IsVisible => Text is not null;

    public ObservableCollection<AlertNotice> Recent { get; } = [];

    public bool HasUnread => UnreadCount > 0;

    public bool IsEmpty => Recent.Count == 0;

    public void Show(AlertNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Show(
            notification.Event.AtUtc,
            notification.Monitor.Name,
            notification.Event.Reason,
            notification.Event.Action != AlertAction.Cleared);
    }

    /// <summary>
    /// Уведомление от самого продукта, а не от монитора.
    /// </summary>
    /// <remarks>
    /// Появилось для повреждения базы (И-24): о нём нельзя молчать до тех пор,
    /// пока оператор сам не откроет журнал и не наткнётся на ошибку.
    /// </remarks>
    public void ShowSystem(string title, string text, bool alarming = true) =>
        Show(DateTimeOffset.UtcNow, title, text, alarming);

    private void Show(DateTimeOffset atUtc, string title, string text, bool alarming)
    {
        Text = $"{title}: {text}";
        IsAlarming = alarming;
        OnPropertyChanged(nameof(IsVisible));

        Recent.Insert(0, new AlertNotice(
            atUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
            title,
            text,
            alarming));

        while (Recent.Count > Capacity)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        UnreadCount++;
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Убирает полосу. Оповещение остаётся и в ленте, и в журнале.</summary>
    public void Dismiss()
    {
        Text = null;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void MarkRead()
    {
        UnreadCount = 0;
        OnPropertyChanged(nameof(HasUnread));
    }

    public void Clear()
    {
        Recent.Clear();
        MarkRead();
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>
/// Оповещение полосой в окне.
/// </summary>
/// <remarks>
/// Значка в трее пока нет намеренно: у продукта ещё нет собственной иконки, а значок
/// без изображения в панели задач Windows попросту не появляется. Обещать канал,
/// который молчит, хуже, чем его не иметь. Полоса в окне делает ровно то, чего от трея
/// ждут в первую очередь, — сообщает, не прерывая работу.
/// </remarks>
public sealed class BannerAlertChannel(NotificationCenter center) : IAlertChannel
{
    private readonly NotificationCenter _center = center ?? throw new ArgumentNullException(nameof(center));

    public string Name => "уведомление";

    public string Title => "Полоса в окне приложения";

    public bool IsConfigured => true;

    public string? MissingConfiguration => null;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return Dispatcher.UIThread.InvokeAsync(() => _center.Show(notification)).GetTask();
    }
}

/// <summary>
/// Оповещение звуком.
/// </summary>
/// <remarks>
/// Системный сигнал через <c>user32!MessageBeep</c>. Вызов лежит здесь, а не
/// в платформенном слое, потому что звук — забота представления: без человека
/// у экрана он не значит ничего. Три строки объявления дешевле, чем порт ради гудка
/// или пакет <c>System.Windows.Extensions</c> ради того же.
/// </remarks>
public sealed class SoundAlertChannel : IAlertChannel
{
    /// <summary>MB_ICONEXCLAMATION — предупреждающий сигнал.</summary>
    private const uint Warning = 0x00000030;

    /// <summary>MB_ICONASTERISK — сигнал «к сведению».</summary>
    private const uint Information = 0x00000040;

    public string Name => "звук";

    public string Title => "Системный сигнал на этой машине";

    public bool IsConfigured => true;

    public string? MissingConfiguration => null;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Возврат намеренно игнорируется: беззвучный режим системы или отсутствие
        // звуковой карты — не повод объявлять доставку алерта неудавшейся.
        _ = MessageBeep(notification.Event.Action == AlertAction.Cleared ? Information : Warning);

        return Task.CompletedTask;
    }

    // Объявление через DllImport, а не LibraryImport: второй требует включить
    // небезопасный код во всём проекте клиента. Платить этим за один гудок незачем,
    // и в платформенном слое по той же причине используется тот же способ.
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
