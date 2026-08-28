using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.App.Views;
using StormMachine.Application.Monitors;
using StormMachine.Composition;

namespace StormMachine.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    private TrayIndicator? _tray;

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

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };

            window.AttachFilePicker(_services.GetRequiredService<FilePicker>());
            desktop.MainWindow = window;

            // Значок в панели задач показывает состояние мониторов постоянно —
            // в отличие от оповещения, которое сообщает о событии один раз.
            _tray = _services.GetRequiredService<TrayIndicator>();
            _tray.ShowRequested += (_, _) => Reveal(window);
            _tray.Attach(desktop);

            // Планировщик мониторов поднимается вместе с окном и работает, пока
            // работает клиент. Разбор пропущенных сроков делается его же силами:
            // продукт мог не работать сутки, и об этом надо узнать при старте,
            // а не при первом взгляде на историю.
            StartScheduler();

            desktop.ShutdownRequested += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Показывает окно по нажатию на значок в трее.</summary>
    /// <remarks>
    /// Три действия, а не одно: свёрнутое окно надо развернуть, скрытое — показать,
    /// а окно за чужим — поднять. Нажатие на значок означает «покажи», и любое
    /// из трёх состояний должно приводить к одному и тому же.
    /// </remarks>
    private static void Reveal(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void StartScheduler()
    {
        var services = _services!;
        var scheduler = services.GetRequiredService<MonitorScheduler>();

        // Не ждём: разбор пропущенного обращается к базе, а окно уже показано.
        // Ошибка здесь не должна валить клиент — без планировщика он остаётся
        // полностью пригоден для ручных измерений.
        _ = Task.Run(async () =>
        {
            try
            {
                await scheduler.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                services
                    .GetRequiredService<ILogger<App>>()
                    .LogError(ex, "Планировщик мониторов не запустился.");
            }
        });
    }

    /// <summary>
    /// Закрытие клиента.
    /// </summary>
    /// <remarks>
    /// Синхронное освобождение контейнера здесь не годится: планировщик умеет
    /// освобождаться только асинхронно — он останавливает цикл и дожидается идущих
    /// проверок. Оборвать проверку на полуслове значило бы потерять уже измеренное.
    /// </remarks>
    private void Shutdown()
    {
        if (_services is null)
        {
            return;
        }

        _tray?.Dispose();
        _tray = null;

        _services.GetRequiredService<MonitorScheduler>().StopAsync().GetAwaiter().GetResult();
        _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services = null;
    }
}
