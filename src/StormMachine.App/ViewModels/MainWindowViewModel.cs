using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Оболочка главного окна: навигация, строка состояния и панель выполняющихся операций.
/// </summary>
/// <remarks>
/// Разделы, которых ещё нет, показываются с честной пометкой об итерации, а не прячутся
/// (UX-принцип 6, docs/01-analysis.md §9.5). Готовые разделы подставляют свои страницы.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<NavigationSection, PageViewModel> _pageFactory;
    private readonly INetworkEnvironment _environment;
    private readonly IHighResolutionClock _clock;
    private readonly Dictionary<string, PageViewModel> _pages = new(StringComparer.Ordinal);

    public MainWindowViewModel(
        RunnerService runner,
        INetworkEnvironment environment,
        IHighResolutionClock clock,
        NotificationCenter notifications,
        Func<NavigationSection, PageViewModel> pageFactory)
    {
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));

        Sections = NavigationMap.Sections;

        // Палитра создаётся оболочкой, потому что переход — её дело. Палитра знает
        // только, что у неё есть способ куда-то отправить, и ничего о навигации.
        Palette = new CommandPaletteViewModel(Sections, GoToWithTarget);

        Runner.ActiveChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasActiveRuns));
            OnPropertyChanged(nameof(ActiveRunsCaption));
        };

        UpdateStatus();
        Navigate(Sections[0]);
    }

    public RunnerService Runner { get; }

    /// <summary>
    /// Центр уведомлений: полоса и лента пропущенного.
    /// </summary>
    /// <remarks>
    /// Живёт в оболочке, а не на экране мониторов: алерт приходит, когда оператор
    /// смотрит на что угодно другое, — иначе он и не был бы нужен.
    /// </remarks>
    public NotificationCenter Notifications { get; }

    public IReadOnlyList<NavigationSection> Sections { get; }

    /// <summary>Палитра команд: Ctrl+K.</summary>
    public CommandPaletteViewModel Palette { get; }

    public ObservableCollection<ActiveOperationViewModel> ActiveRuns => Runner.Active;

    public bool HasActiveRuns => Runner.Active.Count > 0;

    public string ActiveRunsCaption => Runner.Active.Count switch
    {
        0 => "Нет выполняющихся операций",
        1 => "1 выполняющаяся операция",
        _ => $"{Runner.Active.Count} выполняющихся операций",
    };

    [ObservableProperty]
    private NavigationSection? _selectedSection;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private bool _isDrawerOpen;

    [ObservableProperty]
    private bool _isNotificationsOpen;

    // ------------------------------------------------------------- строка состояния

    [ObservableProperty]
    private string _adapterLine = string.Empty;

    [ObservableProperty]
    private string? _adapterWarning;

    [ObservableProperty]
    private string _floorLine = string.Empty;

    public static string WindowTitle => ProductInfo.NameAndVersion;

    public static string LevelText => "Уровень 0 — ядро";

    partial void OnSelectedSectionChanged(NavigationSection? value)
    {
        if (value is not null)
        {
            Navigate(value);
        }
    }

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;

    /// <summary>Убирает полосу оповещения. Событие при этом остаётся в ленте.</summary>
    [RelayCommand]
    private void DismissAlert() => Notifications.Dismiss();

    /// <summary>Открывает ленту уведомлений. Открытие считается прочтением.</summary>
    [RelayCommand]
    private void ToggleNotifications()
    {
        IsNotificationsOpen = !IsNotificationsOpen;

        if (IsNotificationsOpen)
        {
            Notifications.MarkRead();
        }
    }

    [RelayCommand]
    private void ClearNotifications() => Notifications.Clear();

    /// <summary>Переход в раздел из содержимого страницы, а не из бокового меню.</summary>
    [RelayCommand]
    public void GoTo(string? route)
    {
        var section = Sections.FirstOrDefault(s => string.Equals(s.Route, route, StringComparison.Ordinal));

        if (section is not null)
        {
            SelectedSection = section;
        }
    }

    /// <summary>
    /// Переход с подстановкой цели.
    /// </summary>
    /// <remarks>
    /// Страница, умеющая принять цель, получает её после перехода. Не умеющая —
    /// просто открывается: набранный адрес не повод отказать в переходе.
    /// </remarks>
    private void GoToWithTarget(string route, string? target)
    {
        GoTo(route);

        if (target is { Length: > 0 } && CurrentPage is ITargetAware aware)
        {
            aware.UseTarget(target);
        }
    }

    private void Navigate(NavigationSection section)
    {
        CurrentPage?.Deactivate();

        // Страницы переиспользуются: возврат на экран измерений не должен терять
        // введённые параметры и показанный график.
        if (!_pages.TryGetValue(section.Route, out var page))
        {
            page = _pageFactory(section);
            _pages[section.Route] = page;
        }

        CurrentPage = page;

        if (SelectedSection?.Route != section.Route)
        {
            SelectedSection = section;
        }

        UpdateStatus();

        _ = page.ActivateAsync();
    }

    private void UpdateStatus()
    {
        var adapter = _environment.GetPrimaryAdapter();

        if (adapter is null)
        {
            AdapterLine = "Активный адаптер не определён";
            AdapterWarning = null;
        }
        else
        {
            AdapterLine = $"{adapter.Name} · {DescribeKind(adapter.Kind)}"
                          + (adapter.IPv4Address is { } ip ? $" · {ip}" : string.Empty);

            // Предупреждение живёт в строке состояния намеренно: оператор должен
            // видеть его до того, как поверит цифрам, а не после.
            AdapterWarning = adapter.Kind switch
            {
                AdapterKind.Virtual => "измерение через виртуальный коммутатор — он вносит собственный джиттер",
                AdapterKind.Vpn or AdapterKind.Tunnel => "измерение через VPN или туннель — задержка включает шифрование и обходной маршрут",
                _ => null,
            };
        }

        FloorLine = _clock.CalibrationBaselineMs > 0
            ? $"порог {_clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс"
            : "порог не измерен";
    }

    private static string DescribeKind(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "тип не определён",
    };
}
