using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Domain.Monitors;

namespace StormMachine.App.Services;

/// <summary>
/// Значок в панели задач: постоянное состояние мониторов.
/// </summary>
/// <remarks>
/// Не канал оповещения, а <b>показ состояния</b>, и разница существенна. Канал
/// сообщает о событии один раз; значок отвечает на вопрос «а сейчас как» в любую
/// секунду, боковым зрением и не отвлекая. Оповещение о самом событии остаётся
/// за полосой в окне и звуком.
/// <para>
/// Цвет меняется целиком, а не деталью: в шестнадцати пикселях красная вершина
/// заняла бы два пикселя и осталась незамеченной. Меняется вся линия — это
/// единственное, что различимо в панели задач, если туда не всматриваться.
/// </para>
/// <para>
/// Состояние берётся из базы при запуске, а не накапливается с нуля: продукт
/// могли закрыть с поднятым алертом, и значок, начавший спокойным, соврал бы.
/// </para>
/// </remarks>
public sealed class TrayIndicator : IDisposable
{
    private readonly IMonitorStore _store;
    private readonly MonitorScheduler _scheduler;

    private int _refreshing;
    private int _repeat;
    private TrayIcon? _icon;
    private WindowIcon? _calm;
    private WindowIcon? _alarmed;
    private bool _isAlarmed;

    public TrayIndicator(IMonitorStore store, MonitorScheduler scheduler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    /// <summary>Запрос показать главное окно — нажатием на значок или пункт меню.</summary>
    public event EventHandler? ShowRequested;

    public void Attach(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);

        _calm = Load("storm.ico");
        _alarmed = Load("storm-alert.ico");

        var show = new NativeMenuItem("Показать окно");
        show.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var quit = new NativeMenuItem("Выйти");
        quit.Click += (_, _) => desktop.Shutdown();

        _icon = new TrayIcon
        {
            Icon = _calm,
            ToolTipText = "Storm Machine",
            IsVisible = true,
            Menu = [show, new NativeMenuItemSeparator(), quit],
        };

        _icon.Clicked += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        TrayIcon.SetIcons(Avalonia.Application.Current!, [_icon]);

        _scheduler.Alerted += OnAlerted;
        _scheduler.Checked += OnChecked;

        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _scheduler.Alerted -= OnAlerted;
        _scheduler.Checked -= OnChecked;

        // Значок надо убрать явно: без этого он остаётся в панели задач
        // призраком до тех пор, пока по нему не проведут мышью.
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }
    }

    private void OnAlerted(object? sender, AlertEvent alert) => _ = RefreshAsync();

    private void OnChecked(object? sender, MonitorCheck check) => _ = RefreshAsync();

    /// <summary>
    /// Пересчитывает состояние по всем мониторам.
    /// </summary>
    /// <remarks>
    /// Обновления схлопываются. Обработчик зовётся на каждую проверку каждого монитора,
    /// а обход опрашивает хранилище по разу на монитор: двадцать мониторов, идущих
    /// раз в минуту, давали двадцать полных обходов подряд ради одного значка.
    /// Пришедшее во время обхода запоминается и выполняется один раз после него.
    /// </remarks>
    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) == 1)
        {
            Volatile.Write(ref _repeat, 1);
            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref _repeat, 0);

                await RefreshOnceAsync().ConfigureAwait(false);
            }
            while (Volatile.Read(ref _repeat) == 1);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task RefreshOnceAsync()
    {
        try
        {
            var monitors = await _store.ListAsync().ConfigureAwait(false);
            var raised = new List<string>();

            foreach (var monitor in monitors.Where(m => m.IsEnabled))
            {
                var status = await _store.GetStatusAsync(monitor.Id).ConfigureAwait(false);

                if (status.Alert.IsRaised)
                {
                    raised.Add(monitor.Name);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() => Apply(monitors.Count(m => m.IsEnabled), raised));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Значок — удобство. Сбой его обновления не должен трогать ни измерения,
            // ни окно: тревога всё равно придёт полосой и звуком.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_icon is not null)
                {
                    _icon.ToolTipText = $"Storm Machine — состояние неизвестно: {ex.Message}";
                }
            });
        }
    }

    private void Apply(int enabled, List<string> raised)
    {
        if (_icon is null)
        {
            return;
        }

        var alarmed = raised.Count > 0;

        if (alarmed != _isAlarmed)
        {
            _icon.Icon = alarmed ? _alarmed : _calm;
            _isAlarmed = alarmed;
        }

        _icon.ToolTipText = alarmed
            ? $"Storm Machine — тревога: {string.Join(", ", raised.Take(3))}"
              + (raised.Count > 3 ? $" и ещё {raised.Count - 3}" : string.Empty)
            : enabled == 0
                ? "Storm Machine — мониторов нет"
                : $"Storm Machine — мониторов {enabled}, всё в норме";
    }

    private static WindowIcon Load(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://StormMachine/Assets/{name}"));

        return new WindowIcon(stream);
    }
}
