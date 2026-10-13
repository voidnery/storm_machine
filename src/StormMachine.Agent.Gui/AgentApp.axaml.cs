using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StormMachine.Agent.Gui;

public partial class AgentApp : Avalonia.Application, IDisposable
{
    private AgentWindowViewModel? _model;

    /// <summary>Закрытие окна останавливает агента: второй его копии быть не должно.</summary>
    public void Dispose()
    {
        _model?.Dispose();
        _model = null;

        GC.SuppressFinalize(this);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _model = new AgentWindowViewModel();

            desktop.MainWindow = new AgentWindow { DataContext = _model };

            // Агент начинает слушать сразу: окно открыли затем, чтобы он работал,
            // а не чтобы нажать в нём ещё одну кнопку «начать».
            _model.Start(desktop.Args ?? []);

            desktop.ShutdownRequested += (_, _) => _model.Stop();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
