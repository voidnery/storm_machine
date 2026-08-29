using StormMachine.Application.Abstractions;
using System.Globalization;
using StormMachine.Application.Probes;
using StormMachine.Domain.Capabilities;
using StormMachine.Domain.Capture;

namespace StormMachine.Application.Capabilities;

/// <summary>
/// Честная картина того, что продукт может на этой машине.
/// </summary>
/// <remarks>
/// Считается по фактам, а не по намерениям: права процесса, наличие драйвера захвата,
/// сопряжённые агенты, лежащие рядом файлы баз. Один и тот же выпуск на двух машинах
/// умеет разное, и притворяться иначе значило бы обещать за чужую систему.
/// <para>
/// Список проб строится из реестра, а не переписан руками: новая проба появляется
/// здесь сама, вместе со своим требованием прав. Иначе экран возможностей отстал бы
/// от продукта на первой же итерации — и врал бы именно там, где обещает честность.
/// </para>
/// </remarks>
public sealed class CapabilityInspector(
    IProbeRegistry probes,
    ISystemCapabilities system,
    IAgentStore agents,
    INetworkEnvironment environment,
    ICaptureProvider? capture = null,
    ISnmpCredentialStore? snmp = null)
{
    private readonly IProbeRegistry _probes = probes ?? throw new ArgumentNullException(nameof(probes));
    private readonly ISystemCapabilities _system = system ?? throw new ArgumentNullException(nameof(system));
    private readonly IAgentStore _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Плагин захвата. Может отсутствовать: уровень 2 необязателен целиком.</summary>
    private readonly ICaptureProvider? _capture = capture;

    private readonly ISnmpCredentialStore? _snmp = snmp;

    /// <summary>Адрес, откуда берут Npcap. Продукт его не распространяет.</summary>
    public const string CaptureDriverSite = "https://npcap.com";

    public async Task<CapabilityReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var paired = await PairedAsync(cancellationToken).ConfigureAwait(false);

        var found = new List<Capability>();

        found.AddRange(Probes(paired));
        found.AddRange(Core());
        found.Add(await SnmpAsync(cancellationToken).ConfigureAwait(false));
        found.AddRange(Capture());

        return new CapabilityReport
        {
            Capabilities = found,
            IsElevated = _system.IsElevated,
        };
    }

    private async Task<int> PairedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _agents.ListAsync(cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Хранилище может быть недоступно — экран возможностей не тот случай,
            // ради которого стоит падать. Считаем, что агентов нет.
            return 0;
        }
    }

    /// <summary>
    /// Пробы как возможности.
    /// </summary>
    /// <remarks>
    /// Требование прав берётся из объявления самой пробы — того же, из которого
    /// строятся команды консоли и формы интерфейса. Четвёртое применение одного
    /// объявления: добавляя пробу, мы бесплатно получаем и строку на этом экране.
    /// </remarks>
    private IEnumerable<Capability> Probes(int pairedAgents)
    {
        foreach (var descriptor in _probes.Descriptors)
        {
            var state = descriptor.RequiresElevation && !_system.IsElevated
                ? CapabilityState.NeedsElevation
                : descriptor.RequiresAgent && pairedAgents == 0
                    ? CapabilityState.NeedsAgent
                    : CapabilityState.Available;

            yield return new Capability
            {
                Id = $"probe.{descriptor.Name}",
                Title = descriptor.Title,
                About = descriptor.Description,
                Level = CapabilityLevel.Core,
                State = state,
                Detail = state switch
                {
                    CapabilityState.NeedsElevation =>
                        "Проба требует прав администратора, а продукт запущен без них.",
                    CapabilityState.NeedsAgent =>
                        "Нужна вторая точка измерения: сопряжённых агентов нет.",
                    _ => null,
                },
                HowToEnable = state switch
                {
                    CapabilityState.NeedsElevation => "Перезапустить продукт от имени администратора.",
                    CapabilityState.NeedsAgent =>
                        "Поставить storm-agent на второй машине и сопрячь: storm agents pair.",
                    _ => null,
                },
            };
        }
    }

    /// <summary>Возможности ядра, не сводящиеся к одной пробе.</summary>
    private IEnumerable<Capability> Core()
    {
        var adapter = _environment.GetPrimaryAdapter();

        yield return new Capability
        {
            Id = "core.inventory",
            Title = "Инвентарь подсети с MAC-адресами",
            About = "Кто есть в сети: адрес, MAC, вендор, имя. Без прав администратора.",
            Level = CapabilityLevel.Core,
            State = CapabilityState.Available,
        };

        yield return new Capability
        {
            Id = "core.topology",
            Title = "Карта сети (L3)",
            About = "Граф из трассировок, таблицы маршрутизации и ARP. "
                    + "Достоверность каждой связи видна видом линии.",
            Level = CapabilityLevel.Core,
            State = CapabilityState.Limited,
            Detail = "Связи второго уровня выводятся эвристиками: кто с каким портом свитча "
                     + "соединён, само по себе ядро не знает.",

            // До И-17 здесь стояло «появится с уровнем 1». Уровни сделаны, и строка
            // обязана называть действие, а не срок: обещание, пережившее свой срок,
            // хуже отсутствия обещания.
            HowToEnable = "Точную привязку к портам даёт уровень 1: storm snmp creds add. "
                          + "Свой порт на коммутаторе — уровень 2: storm topology --захват 60.",
        };

        yield return new Capability
        {
            Id = "core.raw-sockets",
            Title = "Сырые сокеты ICMP",
            About = "Более точная разметка времени и параллельная трассировка.",
            Level = CapabilityLevel.Core,
            State = _system.CanOpenRawSockets ? CapabilityState.Available : CapabilityState.Limited,
            Detail = _system.CanOpenRawSockets
                ? "Доступны: продукт может открыть сырой сокет."
                : "Недоступны. Измерения идут через системный API — это работает, "
                  + "но разметка времени грубее.",
            HowToEnable = _system.CanOpenRawSockets
                ? null
                : "Обычно помогает запуск от имени администратора. На части систем "
                  + "сырые сокеты закрыты политикой и с ним.",
        };

        yield return new Capability
        {
            Id = "core.timing",
            Title = "Достоверность измерений времени",
            About = "Порог, ниже которого продукт не берётся различать величины.",
            Level = CapabilityLevel.Core,
            State = adapter is null || adapter.Kind is Domain.Measurements.AdapterKind.Physical
                or Domain.Measurements.AdapterKind.Wireless
                    ? CapabilityState.Available
                    : CapabilityState.Limited,
            Detail = adapter is null
                ? "Активный адаптер не определён."
                : adapter.Kind is Domain.Measurements.AdapterKind.Physical
                    or Domain.Measurements.AdapterKind.Wireless
                    ? $"Измерение идёт через {adapter.Name} — абсолютным значениям можно доверять."
                    : $"Измерение идёт через {adapter.Name}: этот адаптер вносит собственную "
                      + "задержку и джиттер. Сравнение между запусками остаётся в силе, "
                      + "абсолютные значения — нет.",
            HowToEnable = adapter is null || adapter.Kind is Domain.Measurements.AdapterKind.Physical
                or Domain.Measurements.AdapterKind.Wireless
                    ? null
                    : "Мерить с машины, подключённой физическим адаптером.",
        };
    }

    /// <summary>
    /// Возможности уровня 1.
    /// </summary>
    /// <remarks>
    /// С И-17 опрос в продукте есть, и состояние определяется одним: заведены ли
    /// учётные данные. Оставить здесь «запланировано» после того, как возможность
    /// сделана, — та же ложь, что и спрятать недоступное: экран возможностей ценен
    /// ровно настолько, насколько ему можно верить.
    /// </remarks>
    private async Task<Capability> SnmpAsync(CancellationToken cancellationToken)
    {
        var configured = 0;

        if (_snmp is not null)
        {
            try
            {
                configured = (await _snmp.ListAsync(cancellationToken).ConfigureAwait(false)).Count;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Хранилище может быть недоступно — сводка возможностей не тот случай,
                // ради которого стоит падать.
                configured = 0;
            }
        }

        return new Capability
        {
            Id = "snmp",
            Title = "SNMP: точная топология и счётчики портов",
            About = "LLDP-MIB даёт, кто с каким портом соединён. BRIDGE-MIB привязывает "
                    + "устройство к порту коммутатора. Счётчики ошибок находят умирающий патч-корд.",
            Level = CapabilityLevel.Snmp,
            State = configured > 0 ? CapabilityState.Available : CapabilityState.NeedsCredentials,
            Detail = configured > 0
                ? $"Заведено наборов учётных данных: {configured.ToString(CultureInfo.InvariantCulture)}."
                : "Учётных данных нет — опрашивать оборудование нечем.",
            HowToEnable = configured > 0
                ? null
                : "Завести набор: storm snmp creds add \"свитчи\" --версия v2c. "
                  + "Пароль спрашивается отдельно и в историю оболочки не попадает.",
        };
    }

    /// <summary>
    /// Возможности уровня 2.
    /// </summary>
    /// <remarks>
    /// С И-18 плагин в продукте есть, и состояние определяется одним: пускает ли нас
    /// драйвер. Разница между «драйвера нет» и «драйвер есть, но не пускает»
    /// существенна — это два разных совета, и склеивать их в «недоступно» значит
    /// заставить человека перебирать оба варианта вслепую.
    /// </remarks>
    private IEnumerable<Capability> Capture()
    {
        var refusal = _capture?.Availability ?? CaptureRefusal.NoDriver;
        var driver = _capture?.DriverDescription;

        var state = refusal switch
        {
            CaptureRefusal.None => CapabilityState.Available,
            CaptureRefusal.NeedsElevation => CapabilityState.NeedsElevation,
            CaptureRefusal.NoAdapters => CapabilityState.Limited,
            _ => CapabilityState.NeedsDriver,
        };

        yield return new Capability
        {
            Id = "capture.neighbors",
            Title = "Соседство по LLDP и CDP из эфира",
            About = "Кто с каким портом соединён — по кадрам, которые устройства "
                    + "объявляют сами. Учётных данных не требует.",
            Level = CapabilityLevel.Capture,
            State = state,
            Detail = Detail(refusal, driver),
            HowToEnable = HowToEnable(refusal),
            Where = refusal == CaptureRefusal.NoDriver ? CaptureDriverSite : null,
        };

        yield return new Capability
        {
            Id = "capture.dhcp",
            Title = "Обнаружение постороннего DHCP",
            About = "Ответы DHCP широковещательны: продукт слушает их и показывает, "
                    + "сколько серверов в сегменте и какой шлюз каждый объявляет.",
            Level = CapabilityLevel.Capture,
            State = state,
            Detail = Detail(refusal, driver),
            HowToEnable = HowToEnable(refusal),
            Where = refusal == CaptureRefusal.NoDriver ? CaptureDriverSite : null,
        };
    }

    private static string Detail(CaptureRefusal refusal, string? driver) => refusal switch
    {
        CaptureRefusal.None => $"Драйвер захвата работает: {driver ?? "версия не определена"}. "
                               + "Продукт слушает без неразборчивого режима — только то, "
                               + "что адресовано нам или разослано всем.",
        CaptureRefusal.NeedsElevation => "Драйвер установлен, но не пускает: Npcap умеет "
                                         + "ставиться с ограничением доступа администраторами.",
        CaptureRefusal.NoAdapters => "Драйвер есть, но подходящих адаптеров он не показывает.",
        _ => "Драйвер захвата на этой машине не установлен.",
    };

    private static string? HowToEnable(CaptureRefusal refusal) => refusal switch
    {
        CaptureRefusal.None => null,
        CaptureRefusal.NeedsElevation => "Перезапустить продукт от имени администратора "
                                         + "либо переустановить Npcap без ограничения доступа.",
        CaptureRefusal.NoAdapters => "Проверить, что адаптер включён и виден системе.",
        _ => "Npcap ставится отдельно и вручную: продукт его не распространяет "
             + "ни при каких условиях — лицензия NPSL это запрещает.",
    };
}
