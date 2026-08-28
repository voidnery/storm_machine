using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Capabilities;

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
    INetworkEnvironment environment)
{
    private readonly IProbeRegistry _probes = probes ?? throw new ArgumentNullException(nameof(probes));
    private readonly ISystemCapabilities _system = system ?? throw new ArgumentNullException(nameof(system));
    private readonly IAgentStore _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Адрес, откуда берут Npcap. Продукт его не распространяет.</summary>
    public const string CaptureDriverSite = "https://npcap.com";

    public async Task<CapabilityReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var paired = await PairedAsync(cancellationToken).ConfigureAwait(false);

        var found = new List<Capability>();

        found.AddRange(Probes(paired));
        found.AddRange(Core());
        found.Add(Snmp());
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
                     + "соединён, без SNMP не узнать.",
            HowToEnable = "Точная L2-топология появится с уровнем 1.",
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

    private static Capability Snmp() => new()
    {
        Id = "snmp",
        Title = "SNMP: точная топология и счётчики портов",
        About = "LLDP-MIB даёт, кто с каким портом соединён. BRIDGE-MIB привязывает "
                + "устройство к порту свитча. Счётчики ошибок находят умирающий патч-корд.",
        Level = CapabilityLevel.Snmp,
        State = CapabilityState.Planned,
        Detail = "В продукте пока нет.",
        Iteration = "И-17",
    };

    private IEnumerable<Capability> Capture()
    {
        var installed = _system.IsCaptureDriverInstalled;

        yield return new Capability
        {
            Id = "capture.plugin",
            Title = "Захват пакетов: LLDP/CDP, пассивный анализ",
            About = "Приём кадров второго уровня напрямую: соседство по LLDP и CDP, "
                    + "обнаружение постороннего DHCP по широковещательным пакетам.",
            Level = CapabilityLevel.Capture,
            State = CapabilityState.Planned,
            Detail = installed
                ? $"Драйвер захвата на машине есть ({_system.CaptureDriverDescription}), "
                  + "но плагина в продукте пока нет."
                : "В продукте пока нет плагина, и драйвер захвата на этой машине не установлен.",
            HowToEnable = installed
                ? null
                : "Npcap ставится отдельно и вручную: продукт его не распространяет "
                  + "ни при каких условиях — лицензия NPSL это запрещает.",
            Where = installed ? null : CaptureDriverSite,
            Iteration = "И-18",
        };
    }
}
