using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Domain.Capabilities;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Оболочка главного окна: навигация, строка состояния и панель выполняющихся операций.
/// </summary>
/// <remarks>
/// Разделы без экранной формы показываются с честной пометкой «консоль», а не прячутся
/// (UX-принцип 6, docs/01-analysis.md §9.5). Готовые разделы подставляют свои страницы.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<NavigationSection, PageViewModel> _pageFactory;
    private readonly INetworkEnvironment _environment;
    private readonly IHighResolutionClock _clock;
    private readonly CapabilityInspector _capabilities;
    private readonly Dictionary<string, PageViewModel> _pages = new(StringComparer.Ordinal);

    /// <summary>Идёт переход — повторный вход через выбор в меню не нужен.</summary>
    private bool _navigating;

    public MainWindowViewModel(
        RunnerService runner,
        INetworkEnvironment environment,
        IHighResolutionClock clock,
        NotificationCenter notifications,
        IDatabaseMaintenance maintenance,
        IStorageLocation location,
        CapabilityInspector capabilities,
        Func<NavigationSection, PageViewModel> pageFactory)
    {
        IsPortable = (location ?? throw new ArgumentNullException(nameof(location))).IsPortable;
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

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

        _ = WarnIfDatabaseDamagedAsync(maintenance ?? throw new ArgumentNullException(nameof(maintenance)));
    }

    /// <summary>
    /// Фоновая проверка целостности базы при старте.
    /// </summary>
    /// <remarks>
    /// Повреждение всплывало только когда оператор сам натыкался на упавший экран —
    /// и выглядело как «SQLite Error 11» (И-24). Проверка идёт в фоне и не задерживает
    /// запуск; её собственный отказ клиента не роняет — он не важнее возможности
    /// работать с целыми частями базы.
    /// </remarks>
    private async Task WarnIfDatabaseDamagedAsync(IDatabaseMaintenance maintenance)
    {
        try
        {
            if (!File.Exists(maintenance.DatabasePath))
            {
                return;
            }

            var health = await Task.Run(() => maintenance.CheckAsync()).ConfigureAwait(true);

            if (health.IsHealthy)
            {
                return;
            }

            Notifications.ShowSystem(
                "База повреждена",
                "файл читается не весь; целые части работают. Закройте клиент и выполните "
                + "storm db repair — читаемое перенесётся в новый файл, повреждённый "
                + "сохранится в резервной папке.");
        }
        catch (Exception)
        {
            // Отказ фоновой проверки не сообщается: он не отличим от «базы ещё нет»
            // и не мешает работе. Настоящая ошибка чтения всплывёт на первом же экране.
        }
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

    /// <summary>
    /// Копия портативная — и это видно, не заходя в настройки.
    /// </summary>
    /// <remarks>
    /// Объявляется по тому же правилу, по которому строка состояния называет
    /// виртуальный коммутатор: показывается необычное. Обычный режим — тот,
    /// в котором продукт установлен и пишет в профиль; портативный меняет ответ
    /// на вопрос «где мои измерения», и узнавать его из настроек поздно.
    /// </remarks>
    public bool IsPortable { get; }

    public static string WindowTitle => ProductInfo.NameAndVersion;

    /// <summary>
    /// Подпись в правом нижнем углу: продукт, версия и кто его выпускает.
    /// </summary>
    /// <remarks>
    /// Версия остаётся рядом с подписью намеренно: по ней разбирают случай
    /// на чужой машине, и убрать её ради красоты значило бы каждый раз спрашивать
    /// «а какая у вас сборка».
    /// </remarks>
    public static string Signature => ProductInfo.Signature;

    /// <summary>
    /// Какие уровни работают на этой машине прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Была константой «Уровень 0 — ядро» и не менялась никогда: ни от запуска
    /// с правами, ни от заведённых учётных данных SNMP. Замечание оператора.
    /// Надпись, которая не может стать другой, не сообщает ничего — а выглядит
    /// как сообщение о состоянии, и в этом её вред.
    /// <para>
    /// Считается по тому же отчёту возможностей, что и экран «Разработка»
    /// и <c>storm capabilities</c>: три места о состоянии машины не вправе
    /// расходиться.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private string _levelText = "Уровень 0 — ядро";

    /// <summary>Что означают уровни и чего не хватает остальным.</summary>
    [ObservableProperty]
    private string _levelHint = LevelBadge.Scale;

    /// <summary>
    /// Пересчитывает уровни по состоянию машины.
    /// </summary>
    /// <remarks>
    /// Зовётся при каждом переходе между разделами, а не один раз на запуск:
    /// учётные данные SNMP заводят в настройках, не закрывая окна, и надпись обязана
    /// это заметить. Отчёт возможностей считается за доли секунды — платить за это
    /// устаревшей надписью незачем.
    /// <para>
    /// Уровни независимы: захват требует прав, SNMP — учётных данных, и одно
    /// без другого бывает. Поэтому показывается не «верхний достигнутый», а те,
    /// что работают: «Уровни 0 и 2» — такое же законное состояние машины,
    /// как и «все три».
    /// </para>
    /// </remarks>
    private async Task RefreshLevelsAsync()
    {
        try
        {
            var report = await _capabilities.InspectAsync().ConfigureAwait(true);

            // Уровень считается работающим, только если работает целиком: это правило
            // домена (CapabilityReport.StateOf), и шапка опирается на него, а не
            // на свою догадку. Уровень, где работает половина, обещал бы SNMP там,
            // где учётных данных нет.
            var snmp = report.StateOf(CapabilityLevel.Snmp) == CapabilityState.Available;
            var capture = report.StateOf(CapabilityLevel.Capture) == CapabilityState.Available;

            LevelText = LevelBadge.Describe(snmp, capture);
            LevelHint = LevelBadge.Hint(snmp, capture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Сводка возможностей не важнее работы продукта: не посчиталась —
            // надпись остаётся прежней, а беда видна там, где её показывают.
        }
    }

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

    /// <summary>
    /// Переход в раздел.
    /// </summary>
    /// <remarks>
    /// Защита от повторного входа обязательна: присвоение <see cref="SelectedSection"/>
    /// ниже само зовёт этот же метод через уведомление об изменении, и на старте
    /// первая страница активировалась дважды — две копии загрузки данных наперегонки
    /// чистили и наполняли одни и те же коллекции.
    /// </remarks>
    private void Navigate(NavigationSection section)
    {
        if (_navigating)
        {
            return;
        }

        _navigating = true;

        try
        {
            NavigateCore(section);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void NavigateCore(NavigationSection section)
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

        // Уровни пересчитываются при переходе: учётные данные заводят в настройках,
        // не закрывая окна, и надпись в шапке обязана это заметить.
        _ = RefreshLevelsAsync();

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
            AdapterLine = $"{adapter.Name} · {AdapterWording.Kind(adapter.Kind)}"
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
            ? $"порог часов {Units.Milliseconds(_clock.CalibrationBaselineMs)}"
            : "порог не измерен";
    }
}
