using Avalonia;

namespace StormMachine.App;

internal static class Program
{
    /// <summary>
    /// Точка входа. Ничего, кроме инициализации Avalonia, здесь быть не должно:
    /// до <see cref="AppBuilder"/> ещё не готова графическая подсистема.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить приложение: {ex}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
