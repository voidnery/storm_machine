using Avalonia;

namespace StormMachine.Agent.Gui;

/// <summary>
/// Точка входа окна агента.
/// </summary>
/// <remarks>
/// Ключи те же, что у консольного агента, и разбираются там же — здесь только порт
/// и признак сопряжения, потому что остальное (служба, список собеседников, дозвон)
/// окну не нужно: это работа, у которой нет картинки.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить окно агента: {ex}");

            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AgentApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
