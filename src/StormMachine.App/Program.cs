using Avalonia;
using Velopack;

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
            // Первой строкой и до Avalonia. Установщик запускает продукт с ключами
            // вроде --veloapp-install, чтобы тот создал ярлыки и завершился; окно
            // при этом появляться не должно, а появится, если сначала поднять Avalonia.
            VelopackApp.Build().Run();

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
