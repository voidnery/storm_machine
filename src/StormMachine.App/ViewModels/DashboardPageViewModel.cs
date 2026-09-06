using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Controls;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Adapters;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка адаптеров на дашборде.</summary>
public sealed record AdapterRow(string Name, string Kind, string Address, bool IsPrimary, bool IsSuspect);

/// <summary>
/// Адаптер в выпадающем списке «меряем отсюда».
/// </summary>
/// <remarks>
/// Пустое опознание — пункт «автоматически»: он первый в списке и остаётся
/// умолчанием продукта. Подпись пункта несёт вид адаптера и адрес: выбирать
/// «Ethernet 2» из четырёх одинаковых имён по одному имени невозможно.
/// </remarks>
public sealed record AdapterOption(string? Id, string Caption, string? About, string? Note) : IOption
{
    public override string ToString() => Caption;

    string IOption.Caption => Caption;

    string? IOption.About => About;

    string? IOption.Note => Note;
}

/// <summary>
/// Одна точка тренда — один прогон из журнала.
/// </summary>
/// <remarks>
/// Медиана и доля ответов берутся из сводки прогона, а не из сырых сэмплов:
/// сводка переживает уборку по политике хранения, сэмплы — нет. График за месяц,
/// пустеющий на второй неделе, был бы хуже отсутствия графика.
/// </remarks>
public sealed record TrendPoint(DateTimeOffset When, double? MedianMs, double? SuccessShare, string Target);

/// <summary>
/// Проба в выпадающем списке тренда.
/// </summary>
/// <remarks>
/// Пробы не смешиваются на одном графике намеренно. Время оборота ping и время
/// первого байта http — разные величины в одной единице; положенные на одну ось,
/// они дают линию, у которой нет предмета. То же правило продукт соблюдает
/// в разборе сценария: «столбики сравнимы между собой, но не складываются».
/// </remarks>
public sealed record TrendProbeOption(string Name, string Caption, string? About) : IOption
{
    public override string ToString() => Caption;

    string IOption.Caption => Caption;

    string? IOption.About => About;
}

/// <summary>
/// Дашборд: состояние окружения и последние прогоны.
/// </summary>
/// <remarks>
/// В И-4 показывает то, что уже есть: сетевое окружение, порог разрешения таймера
/// и журнал. Мониторы и алерты придут в И-14 — и до тех пор раздел честно об этом говорит,
/// а не изображает пустые панели.
/// </remarks>
public sealed partial class DashboardPageViewModel(
    NavigationSection section,
    ChosenAdapterEnvironment environment,
    AdapterChoice choice,
    IHighResolutionClock clock,
    IRunStore store,
    IDeviceStore devices) : PageViewModel(section)
{
    private readonly ChosenAdapterEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly AdapterChoice _choice = choice ?? throw new ArgumentNullException(nameof(choice));

    /// <summary>Подстановка выбора в список не должна выглядеть как выбор оператора.</summary>
    private bool _syncing;
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDeviceStore _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    public ObservableCollection<AdapterRow> Adapters { get; } = [];

    /// <summary>
    /// Что рисуют графики. Пусто — рисовать нечего, и панель прячется.
    /// </summary>
    /// <remarks>
    /// Отдаётся как список, а не как событие с данными: рисование живёт в файле
    /// страницы, типы рисовалки за слой представления не выходят. Модель говорит
    /// «числа сменились», страница решает, чем их изобразить.
    /// </remarks>
    public IReadOnlyList<TrendPoint> Trend { get; private set; } = [];

    /// <summary>Числа тренда сменились — перерисовать.</summary>
    public event EventHandler? TrendUpdated;

    /// <summary>Пробы, по которым в журнале есть прогоны.</summary>
    public ObservableCollection<TrendProbeOption> TrendProbes { get; } = [];

    [ObservableProperty]
    private TrendProbeOption? _trendProbe;

    [ObservableProperty]
    private string _trendSummary = string.Empty;

    [ObservableProperty]
    private string _trendUnit = "мс";

    /// <summary>Есть ли что рисовать: на пустом журнале графики не показываются вовсе.</summary>
    [ObservableProperty]
    private bool _hasTrend;

    partial void OnTrendProbeChanged(TrendProbeOption? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        _ = LoadTrendAsync(value.Name, CancellationToken.None);
    }

    /// <summary>Чем заполнен выпадающий список «меряем отсюда».</summary>
    public ObservableCollection<AdapterOption> AdapterChoices { get; } = [];

    /// <summary>
    /// Выбранный оператором адаптер.
    /// </summary>
    /// <remarks>
    /// Выбор сохраняется сразу, без кнопки «применить»: он действует на следующее
    /// измерение, отменяется тем же списком и ничего не портит. Кнопка здесь была бы
    /// лишним шагом между решением и его исполнением.
    /// </remarks>
    [ObservableProperty]
    private AdapterOption? _measureFrom;

    /// <summary>Что делает выбор адаптера — тезисом.</summary>
    public static string AdapterNote =>
        "Выбор меняет, от какого адаптера продукт считает себя измеряющим: его адрес "
        + "в заголовке прогона и в отчёте, его подсеть — в обнаружении и на карте.";

    /// <summary>
    /// И чего он не делает. Обоснование раскрывается по кнопке карточки.
    /// </summary>
    /// <remarks>
    /// Сказано прямо и в продукте, а не только в документации: человек, выбравший
    /// физическую карту вместо виртуального коммутатора, иначе решит, что и пакеты
    /// пошли через неё. Они пойдут туда, куда велит таблица маршрутов, а пробы ICMP
    /// вдобавок идут через системный Ping, у которого источник не задаётся вовсе.
    /// </remarks>
    public static string AdapterNoteWhy =>
        "Куда пойдёт пакет, решает таблица маршрутов операционной системы, и выбор "
        + "здесь её не меняет: до целей за пределами своей сети трафик уйдёт через "
        + "маршрут по умолчанию, даже если выбран другой адаптер. Пробы ICMP (ping, "
        + "трассировка) идут через системный вызов, у которого адаптер-источник "
        + "не задаётся в принципе. Поэтому продукт говорит вслух, когда выбранный "
        + "адаптер расходится с маршрутным, — числа в этом случае относятся к своей "
        + "сети, а не к пути наружу.";

    /// <summary>Расхождение выбора с маршрутом; пусто — всё сходится.</summary>
    [ObservableProperty]
    private string? _adapterNotice;

    partial void OnMeasureFromChanged(AdapterOption? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        _ = ApplyChoiceAsync(value.Id);
    }

    /// <summary>
    /// Запоминает выбор и перечитывает окружение.
    /// </summary>
    /// <remarks>
    /// Отказ записи не молчит: список показал бы выбранное, а меряли бы по-прежнему
    /// от маршрутного адаптера — ровно то расхождение между показанным и настоящим,
    /// которого продукт не допускает нигде.
    /// </remarks>
    private async Task ApplyChoiceAsync(string? id)
    {
        try
        {
            await _choice.ChooseAsync(id).ConfigureAwait(true);
            await ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AdapterNotice = Services.Trouble.Say(ex);
        }
    }

    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    [ObservableProperty]
    private string _privileges = string.Empty;

    [ObservableProperty]
    private string _timerInfo = string.Empty;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string _journalInfo = string.Empty;

    // Сводка журнала плитками: число отдельно, совет отдельно.

    [ObservableProperty]
    private bool _hasJournal;

    [ObservableProperty]
    private string _runCountText = "—";

    [ObservableProperty]
    private string _sampleCountText = "—";

    [ObservableProperty]
    private string _sizeText = "—";

    /// <summary>
    /// Первый запуск: инвентарь пуст и оператору некуда смотреть.
    /// </summary>
    /// <remarks>
    /// Требование итерации И-8: путь от «запустил» до «вижу свою сеть» не должен
    /// требовать чтения документации. Поэтому на пустом инвентаре дашборд не показывает
    /// пустые панели, а прямо предлагает единственное осмысленное первое действие.
    /// </remarks>
    [ObservableProperty]
    private bool _isFirstRun;

    [ObservableProperty]
    private string _firstRunHint = string.Empty;

    [ObservableProperty]
    private string _inventoryInfo = string.Empty;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        Privileges = _environment.IsElevated
            ? "Права администратора: есть"
            : "Права администратора: нет — уровню 0 они и не требуются";

        // «Порог часов 0.000 мс» читается как измеренный ноль, хотя означает, что
        // калибровки ещё не было: она идёт перед первым измерением. Строка состояния
        // об этом говорила прямо, дашборд — врал числом.
        TimerInfo =
            $"Таймер: разрешение {_clock.ResolutionNanoseconds:0.###} нс, "
            + (_clock.CalibrationBaselineMs > 0
                ? $"порог часов {_clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс"
                : "порог часов ещё не измерен — калибровка идёт перед первым измерением");

        Adapters.Clear();
        var primary = _environment.GetPrimaryAdapter();
        var routed = _environment.Routed;

        // Список выбора собирается заново вместе со списком адаптеров: карту могли
        // вынуть или добавить, пока окно было открыто.
        _syncing = true;
        AdapterChoices.Clear();
        AdapterChoices.Add(new AdapterOption(
            null,
            "Автоматически",
            routed is null
                ? "По маршруту по умолчанию — сейчас он не определён"
                : $"По маршруту по умолчанию — сейчас это «{routed.Name}»",
            null));

        foreach (var adapter in _environment.GetAdapters().Where(a => a.IsUp && a.IPv4Address is not null))
        {
            var suspect = AdapterWording.IsUntrustworthy(adapter.Kind);

            Adapters.Add(new AdapterRow(
                adapter.Name,
                AdapterWording.Kind(adapter.Kind),
                adapter.SubnetCidr ?? adapter.IPv4Address ?? "—",
                primary is not null && primary.Id == adapter.Id,
                suspect));

            AdapterChoices.Add(new AdapterOption(
                adapter.Id,
                adapter.Name,
                $"{AdapterWording.Kind(adapter.Kind)} · {adapter.SubnetCidr ?? adapter.IPv4Address}",

                // Помечен тот, которым система пользуется сама, и тот, чьим числам
                // нельзя верить как своим: и то и другое нужно знать до выбора.
                routed is not null && routed.Id == adapter.Id
                    ? "по маршруту"
                    : suspect ? "свой шум" : null));
        }

        MeasureFrom = AdapterChoices.FirstOrDefault(o => o.Id == _choice.ChosenId) ?? AdapterChoices[0];
        _syncing = false;

        AdapterNotice = _environment.Notice;

        Warning = primary is null
            ? "Активный адаптер не определён — измерения будут без указания интерфейса."
            : AdapterWording.IsUntrustworthy(primary.Kind)
                ? "Измерение пойдёт через виртуальный коммутатор или VPN. Он вносит собственную задержку и джиттер — выбросы могут не иметь отношения к тестируемой сети."
                : null;

        await LoadJournalAsync(cancellationToken).ConfigureAwait(true);
        await LoadTrendAsync(TrendProbe?.Name, cancellationToken).ConfigureAwait(true);
        await LoadInventoryAsync(primary, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Тренд по журналу: как ведут себя задержка и доля ответов от прогона к прогону.
    /// </summary>
    /// <remarks>
    /// Замечание оператора: дашборд показывал состояние машины и список последних
    /// прогонов, но не показывал главного — как сеть ведёт себя во времени. Список
    /// из восьми строк отвечает на «что мерили», а не на «стало хуже или нет»,
    /// и ответ на второй вопрос приходилось собирать глазами по столбику чисел.
    /// <para>
    /// Берётся месяц и не больше двухсот прогонов: дашборд открывается первым,
    /// и вычитывать на нём годовую историю значило бы платить ожиданием за то,
    /// на что всё равно не смотрят с первого экрана. Глубже смотрят в «Журнале».
    /// </para>
    /// </remarks>
    private async Task LoadTrendAsync(string? probeName, CancellationToken cancellationToken)
    {
        try
        {
            var runs = await _store
                .ListAsync(
                    new RunQuery
                    {
                        Limit = 200,
                        Since = DateTimeOffset.UtcNow - TrendWindow,
                        ProbeName = probeName,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            // Список выбора собирается по тому, что в журнале есть: предлагать пробу,
            // которой ни разу не запускали, значит предлагать пустой график.
            if (probeName is null)
            {
                await FillTrendProbesAsync(cancellationToken).ConfigureAwait(true);
                probeName = TrendProbe?.Name;

                if (probeName is not null)
                {
                    runs = await _store
                        .ListAsync(
                            new RunQuery
                            {
                                Limit = 200,
                                Since = DateTimeOffset.UtcNow - TrendWindow,
                                ProbeName = probeName,
                            },
                            cancellationToken)
                        .ConfigureAwait(true);
                }
            }

            // Журнал отдаёт новые сверху; графику нужно слева направо по времени.
            Trend =
            [
                .. runs
                    .Where(r => r.MedianMs is not null || r.SentCount > 0)
                    .OrderBy(r => r.StartedUtc)
                    .Select(r => new TrendPoint(
                        r.StartedUtc,
                        r.MedianMs,
                        r.SentCount > 0 ? (double)r.SuccessCount / r.SentCount : null,
                        r.TargetDisplay)),
            ];

            HasTrend = Trend.Count > 1;
            TrendSummary = Describe(Trend);

            TrendUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Тренд — не условие работы дашборда: не построился график — остаются
            // и окружение, и журнал. Молчать при этом нельзя.
            HasTrend = false;
            TrendSummary = "Тренд не построился: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
        }
    }

    /// <summary>Сколько истории берёт дашборд.</summary>
    private static readonly TimeSpan TrendWindow = TimeSpan.FromDays(30);

    private async Task FillTrendProbesAsync(CancellationToken cancellationToken)
    {
        var recent = await _store
            .ListAsync(new RunQuery { Limit = 400, Since = DateTimeOffset.UtcNow - TrendWindow }, cancellationToken)
            .ConfigureAwait(true);

        var byProbe = recent
            .GroupBy(r => r.ProbeName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var chosen = TrendProbe?.Name;

        _syncing = true;
        TrendProbes.Clear();

        foreach (var group in byProbe)
        {
            var last = group.Max(r => r.StartedUtc).ToLocalTime();

            TrendProbes.Add(new TrendProbeOption(
                group.Key,
                group.Key,
                $"прогонов {group.Count()}, последний {last:dd.MM HH:mm}"));
        }

        TrendProbe = TrendProbes.FirstOrDefault(p =>
                         string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase))
                     ?? TrendProbes.FirstOrDefault();
        _syncing = false;
    }

    /// <summary>
    /// Итог тренда словами.
    /// </summary>
    /// <remarks>
    /// График показывает форму, слова — величину: «медиана 0.5 мс, худший прогон
    /// 21 мс» человек запоминает, а положение точки на оси — нет.
    /// </remarks>
    private static string Describe(IReadOnlyList<TrendPoint> trend)
    {
        if (trend.Count == 0)
        {
            return "За месяц прогонов не было.";
        }

        var measured = trend.Where(p => p.MedianMs is not null).Select(p => p.MedianMs!.Value).ToList();
        var answered = trend.Where(p => p.SuccessShare is not null).Select(p => p.SuccessShare!.Value).ToList();

        var parts = new List<string> { $"прогонов {trend.Count}" };

        if (measured.Count > 0)
        {
            parts.Add($"обычно {Units.Milliseconds(Median(measured))}");
            parts.Add($"худший {Units.Milliseconds(measured.Max())}");
        }

        if (answered.Count > 0)
        {
            var share = answered.Average();

            parts.Add(share >= 1
                ? "ответы без потерь"
                : $"ответов {(share * 100).ToString("0.#", CultureInfo.InvariantCulture)} %");
        }

        return "За месяц: " + string.Join(", ", parts) + ".";
    }

    private static double Median(List<double> values)
    {
        values.Sort();

        return values.Count % 2 == 1
            ? values[values.Count / 2]
            : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
    }

    /// <summary>Сколько устройств известно и что предложить, если ни одного.</summary>
    private async Task LoadInventoryAsync(NetworkAdapter? primary, CancellationToken cancellationToken)
    {
        try
        {
            await _devices.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var known = await _devices.ListDevicesAsync(cancellationToken).ConfigureAwait(true);

            IsFirstRun = known.Count == 0;
            InventoryInfo = known.Count == 0
                ? "Инвентарь пуст."
                : $"В инвентаре {known.Count} устройств, отвечали в последний раз "
                  + $"{known.Count(d => d.IsOnline)}.";

            FirstRunHint = primary?.SubnetCidr is { } subnet
                ? $"Похоже, это первый запуск. Начните с того, что покажет вашу сеть целиком: "
                  + $"сканирование подсети {subnet}. Оно займёт несколько секунд, не требует прав "
                  + "администратора и найдёт даже те узлы, что молчат на ping."
                : "Похоже, это первый запуск. Начните со сканирования своей сети — "
                  + "оно займёт несколько секунд и не требует прав администратора.";
        }
        catch (Exception ex)
        {
            InventoryInfo = $"Инвентарь недоступен: {ex.Message}";
            IsFirstRun = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ActivateAsync().ConfigureAwait(true);

    private async Task LoadJournalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var runs = await _store
                .ListAsync(new RunQuery { Limit = 8 }, cancellationToken)
                .ConfigureAwait(true);

            RecentRuns.Clear();
            foreach (var run in runs)
            {
                RecentRuns.Add(run);
            }
        }
        catch (Exception ex)
        {
            JournalInfo = "Журнал не прочитался: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
            return;
        }

        // Сводка считается отдельно от списка: её отказ — это отказ сводки.
        // Однажды упавший счётчик объявил недоступным журнал, который был загружен
        // и виден на экране, — надпись врала (И-24).
        try
        {
            var usage = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(true);

            // Числа — плитками, совет — отдельной строкой: склеенные в одну серую
            // фразу, они читались как одна надпись, и ни числа, ни совета в ней
            // видно не было.
            HasJournal = usage.RunCount > 0;

            RunCountText = usage.RunCount.ToString("N0", CultureInfo.InvariantCulture);
            SampleCountText = usage.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
            SizeText = (usage.SizeBytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " МБ";

            JournalInfo = usage.RunCount == 0
                ? "Журнал пуст. Запусти измерение — прогоны сохраняются автоматически."
                : string.Empty;
        }
        catch (Exception ex)
        {
            JournalInfo = "Сводка журнала не посчиталась: " + (StorageProblem.ExplainCorruption(ex) ?? ex.Message);
        }
    }
}
