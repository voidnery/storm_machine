using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.App.Views;
using StormMachine.Composition;

namespace StormMachine.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Build();

            // Подготовка ядра выполняется до создания окна и намеренно синхронно:
            // калибровка порога разрешения и открытие журнала занимают доли секунды,
            // а окно, показанное до их завершения, соврало бы в строке состояния —
            // порог был бы нулевым, адаптер неизвестным. Требование «интерфейс
            // не блокируется» относится к работе, а не к запуску: интерфейса ещё нет.
            _services.InitializeStormMachineAsync().GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };

            desktop.ShutdownRequested += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
