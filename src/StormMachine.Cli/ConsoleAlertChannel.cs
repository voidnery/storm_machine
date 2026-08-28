using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;

namespace StormMachine.Cli;

/// <summary>
/// Оповещение в терминал.
/// </summary>
/// <remarks>
/// Существует ради <c>storm monitors watch</c>: когда сторож работает в открытом окне,
/// естественное место для алерта — это окно, а не почта. Настраивать нечего, поэтому
/// канал всегда готов.
/// </remarks>
internal sealed class ConsoleAlertChannel : IAlertChannel
{
    public string Name => "консоль";

    public string Title => "Строка в терминале там, где запущен сторож";

    public bool IsConfigured => true;

    public string? MissingConfiguration => null;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var previous = Console.ForegroundColor;

        try
        {
            Console.ForegroundColor = notification.Event.Action == AlertAction.Cleared
                ? ConsoleColor.Green
                : ConsoleColor.Red;

            Console.WriteLine();
            Console.WriteLine(notification.Subject);
            Console.WriteLine($"  {notification.Event.Reason}");
            Console.WriteLine($"  {notification.Check.Summary}");
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = previous;
        }

        return Task.CompletedTask;
    }
}
