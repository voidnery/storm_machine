using System.Globalization;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;

namespace StormMachine.Domain.Topology;

/// <summary>Что за узел на карте.</summary>
public enum TopologyNodeKind
{
    /// <summary>Машина, с которой ведутся измерения.</summary>
    ThisMachine,

    /// <summary>Широковещательный домен — то, что мы честно можем утверждать про L2.</summary>
    Subnet,

    Router,

    /// <summary>Коммутатор: опрошен по SNMP или объявлен соседом по LLDP.</summary>
    Switch,

    Host,

    /// <summary>Свёрнутая группа конечных узлов: «ещё 240 устройств».</summary>
    HostGroup,

    /// <summary>Узел за пределами наших сетей, встреченный в трассировке.</summary>
    ExternalHop,

    Internet,
}

/// <summary>
/// Насколько мы уверены в связи.
/// </summary>
/// <remarks>
/// Главное различие на карте, и оно обязано быть видимым. Карта, где догадка выглядит
/// как факт, хуже отсутствия карты: по ней принимают решения, не зная, что часть
/// нарисованного — предположение инструмента.
/// </remarks>
public enum LinkConfidence
{
    /// <summary>
    /// Прямое свидетельство. Ответ на ARP означает общий широковещательный домен —
    /// это факт, а не вывод.
    /// </summary>
    Confirmed,

    /// <summary>
    /// Выведено по правилу: шлюз по умолчанию, соседние хопы трассировки.
    /// Правило разумное, но исключения существуют.
    /// </summary>
    Inferred,

    /// <summary>
    /// Допущение по косвенным признакам: адрес попадает в диапазон подсети,
    /// но подтверждения на втором уровне нет.
    /// </summary>
    Assumed,
}

/// <summary>Чем связаны узлы.</summary>
public enum LinkKind
{
    /// <summary>Один широковещательный домен.</summary>
    Layer2,

    /// <summary>Маршрутизация: трафик идёт через этот узел.</summary>
    Routed,

    /// <summary>Соседство по трассировке.</summary>
    Path,
}

/// <summary>Узел карты.</summary>
public sealed record TopologyNode
{
    public required string Id { get; init; }

    public required TopologyNodeKind Kind { get; init; }

    public required string Label { get; init; }

    public string? Address { get; init; }

    public string? MacAddress { get; init; }

    public string? Vendor { get; init; }

    /// <summary>
    /// Роль устройства из инвентаря — тег категории на карте (И-24).
    /// </summary>
    /// <remarks>
    /// Догадка классификатора приходит уже с вопросительным знаком
    /// (<see cref="Discovery.Device.RoleDisplay" />): карта не имеет права
    /// показать догадку тем же словом, что и правку оператора.
    /// </remarks>
    public string? Role { get; init; }

    /// <summary>Сколько устройств свёрнуто в этот узел. 0 — узел не свёрнутый.</summary>
    public int GroupSize { get; init; }

    public bool IsOnline { get; init; } = true;

    /// <summary>Строка подробностей для подсказки.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// VLAN, в которой узел виден коммутатору. <c>null</c> — неизвестна.
    /// </summary>
    /// <remarks>
    /// Появилась в И-23. До неё номер VLAN читался из таблицы пересылки, показывался
    /// в выводе SNMP и <b>терялся на карте</b>: два устройства в разных VLAN на одном
    /// коммутаторе выглядели соседями. Это не пробел показа, а неверное утверждение:
    /// разные VLAN — разные широковещательные домены, и увидеть друг друга эти два
    /// устройства не могут.
    /// <para>
    /// Пусто там, где номер неизвестен, и это не то же самое, что «VLAN 1». Устройство
    /// на коммутаторе без Q-BRIDGE-MIB и устройство в первой VLAN различимы, и сводить
    /// их к одному значению значило бы выдумать сведения.
    /// </para>
    /// </remarks>
    public int? Vlan { get; init; }
}

/// <summary>
/// Связь между узлами карты.
/// </summary>
/// <param name="Because">
/// Почему связь нарисована. Показывается оператору: догадка обязана себя объяснять,
/// иначе её нельзя ни проверить, ни оспорить.
/// </param>
public sealed record TopologyLink(
    string From,
    string To,
    LinkKind Kind,
    LinkConfidence Confidence,
    string Because);

/// <summary>Локальная сеть, к которой подключена машина.</summary>
public sealed record LocalSubnet
{
    public required string Cidr { get; init; }

    public required string InterfaceName { get; init; }

    public string? InterfaceAddress { get; init; }

    public IReadOnlyList<string> Gateways { get; init; } = [];

    /// <summary>Виртуальный коммутатор или VPN — на карте это стоит различать.</summary>
    public bool IsVirtual { get; init; }
}

/// <summary>Наблюдённый путь до внешнего узла — из сохранённой трассировки.</summary>
public sealed record PathObservation
{
    public required string Destination { get; init; }

    /// <summary>Адреса ответивших хопов по порядку. Молчащие хопы пропущены.</summary>
    public required IReadOnlyList<string> Hops { get; init; }

    public required DateTimeOffset ObservedUtc { get; init; }

    /// <summary>В трассировке были молчащие хопы — соседство хопов под вопросом.</summary>
    public bool HasGaps { get; init; }
}

/// <summary>Из чего строится карта.</summary>
public sealed record TopologyInput
{
    public IReadOnlyList<Device> Devices { get; init; } = [];

    public IReadOnlyList<LocalSubnet> Subnets { get; init; } = [];

    public IReadOnlyList<PathObservation> Paths { get; init; } = [];

    /// <summary>
    /// Сколько конечных узлов показывать поимённо, прежде чем свернуть остальные.
    /// </summary>
    /// <remarks>
    /// Порог из спайка-04: триста отдельных прямоугольников с адресами — не карта,
    /// а список, выложенный в строку. Показывать имеет смысл структуру, а листья
    /// сворачивать в счётчик и разворачивать по требованию.
    /// </remarks>
    public int CollapseThreshold { get; init; } = 12;

    /// <summary>Подсети, которые оператор развернул целиком.</summary>
    public IReadOnlyList<string> ExpandedSubnets { get; init; } = [];

    /// <summary>
    /// Устройства, опрошенные по SNMP.
    /// </summary>
    /// <remarks>
    /// Ради них и делался уровень 1. Без них карта отвечает «эти узлы в одном
    /// широковещательном домене», с ними — «это устройство воткнуто вот в этот порт
    /// вот этого коммутатора». Разница между догадкой и фактом, и на карте она
    /// обязана быть видна.
    /// </remarks>
    public IReadOnlyList<SnmpDevice> Switches { get; init; } = [];

    /// <summary>
    /// Соседи, услышанные нашим адаптером.
    /// </summary>
    /// <remarks>
    /// Уровень 2, и он отвечает на вопрос, который не берёт ни один другой источник:
    /// <b>в какой порт какого коммутатора воткнуты мы сами</b>. SNMP на это ответить
    /// не может, пока к коммутатору нет учётных данных, а ARP и трассировка не знают
    /// про порты вовсе.
    /// </remarks>
    public IReadOnlyList<LinkNeighbor> Neighbors { get; init; } = [];

    /// <summary>
    /// Правки оператора: связи, которых инструмент не увидел, и связи, которые он
    /// вывел ошибочно.
    /// </summary>
    /// <remarks>
    /// Применяются последними и перекрывают наблюдения: у человека, который видел
    /// провод, свидетельство весомее любой эвристики.
    /// </remarks>
    public IReadOnlyList<TopologyEdit> Edits { get; init; } = [];
}

/// <summary>
/// Карта сети.
/// </summary>
/// <remarks>
/// Граф <b>вычисляется</b> из свидетельств, а не хранится. Пересчёт — детерминированная
/// функция от набора: одни и те же данные всегда дают одну и ту же карту, независимо
/// от порядка их поступления. Отсюда главное свойство, ради которого всё и устроено
/// именно так: повторное сканирование не может затереть правку оператора, потому что
/// правка — тоже свидетельство, только с наивысшим весом.
/// <para>
/// Второе обязательное свойство — <b>видимая достоверность</b>. Карта, на которой
/// догадка выглядит как факт, хуже отсутствия карты: по ней принимают решения, не зная,
/// что часть нарисованного инструмент домыслил. Поэтому у каждой связи есть уровень
/// уверенности и строка «почему».
/// </para>
/// </remarks>
public sealed record TopologyGraph
{
    /// <summary>Обозначение внешнего мира.</summary>
    public const string InternetId = "internet";

    /// <summary>Обозначение машины оператора.</summary>
    public const string ThisMachineId = "this";

    public required IReadOnlyList<TopologyNode> Nodes { get; init; }

    public required IReadOnlyList<TopologyLink> Links { get; init; }

    /// <summary>
    /// Оговорки к карте: где её нельзя читать буквально.
    /// </summary>
    /// <remarks>
    /// Появились в И-23 ради VLAN, и это первый случай, когда карта обязана сказать
    /// о себе то, чего не видно из её рисунка. Уровень уверенности отвечает на вопрос
    /// «эта связь настоящая»; оговорка — на другой: «связи нарисованы верно, а вот
    /// соседями эти узлы не являются».
    /// <para>
    /// Разные VLAN на одном коммутаторе — разные широковещательные домены. Устройства
    /// в них не видят друг друга, но на карте висят на одном узле и читаются соседями.
    /// Перестроить рисунок так, чтобы это было видно, значило бы завести отдельный узел
    /// на каждую VLAN — и потерять главное, что карта показывает: физическую структуру.
    /// Сказать словами дешевле и честнее.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    public bool IsEmpty => Nodes.Count == 0;

    public int ConfirmedLinks => Links.Count(l => l.Confidence == LinkConfidence.Confirmed);

    public int InferredLinks => Links.Count(l => l.Confidence != LinkConfidence.Confirmed);

    public static TopologyGraph Empty { get; } = new() { Nodes = [], Links = [] };

    /// <summary>
    /// Сколько узлов ещё читаемо на одном листе A4.
    /// </summary>
    /// <remarks>
    /// Полсотни ложатся читаемо, две сотни — уже нет: схема вписывается в ширину
    /// страницы целиком, и подписи на ней становятся мельче того, что глаз разбирает.
    /// Порог измерен глазами на печати, а не выведен из размеров шрифта.
    /// </remarks>
    public const int ReadableNodes = 60;

    /// <summary>Схема не помещается на лист читаемо.</summary>
    public bool IsTooLargeForOnePage => Nodes.Count > ReadableNodes;

    /// <summary>
    /// Делит карту по подсетям — по листу на подсеть.
    /// </summary>
    /// <remarks>
    /// Долг И-15: схема сети в отчёте не масштабировалась под большие сети. Разбиение
    /// именно по подсетям, а не механической нарезкой на плитки: оператор думает о своей
    /// сети подсетями, и лист, на котором половина одной и четверть другой, читается
    /// хуже целого.
    /// <para>
    /// Узлы, не принадлежащие ни одной подсети — интернет, внешние хопы, сама машина, —
    /// попадают на каждый лист: без них лист теряет то, ради чего карта и рисуется,
    /// а именно куда эта подсеть выходит.
    /// </para>
    /// <para>
    /// Карта, помещающаяся на лист, не делится: разбиение полезно ровно тогда, когда
    /// без него не прочесть, и дробить читаемое значило бы усложнить без причины.
    /// </para>
    /// </remarks>
    public IReadOnlyList<(string Title, TopologyGraph Graph)> SplitBySubnet()
    {
        if (!IsTooLargeForOnePage)
        {
            return [];
        }

        var subnets = Nodes.Where(n => n.Kind == TopologyNodeKind.Subnet).ToList();

        if (subnets.Count < 2)
        {
            return [];
        }

        // Узлы вне подсетей едут на каждый лист: без интернета и своей машины
        // лист не отвечает на вопрос «куда эта подсеть выходит».
        var shared = Nodes
            .Where(n => n.Kind is TopologyNodeKind.Internet or TopologyNodeKind.ThisMachine
                                or TopologyNodeKind.ExternalHop or TopologyNodeKind.Router)
            .ToList();

        var sheets = new List<(string Title, TopologyGraph Graph)>();

        foreach (var subnet in subnets)
        {
            // Соседи подсети — то, что к ней подключено напрямую, плюс она сама.
            var attached = Links
                .Where(l => l.From == subnet.Id || l.To == subnet.Id)
                .Select(l => l.From == subnet.Id ? l.To : l.From)
                .ToHashSet(StringComparer.Ordinal);

            attached.Add(subnet.Id);

            // Коммутаторы тянут за собой то, что воткнуто в них: иначе лист покажет
            // коммутатор без единого устройства и соврёт про пустой порт.
            foreach (var id in attached.ToList())
            {
                if (_nodeKind(id) != TopologyNodeKind.Switch)
                {
                    continue;
                }

                foreach (var link in Links.Where(l => l.From == id || l.To == id))
                {
                    attached.Add(link.From == id ? link.To : link.From);
                }
            }

            foreach (var node in shared)
            {
                attached.Add(node.Id);
            }

            var nodes = Nodes.Where(n => attached.Contains(n.Id)).ToList();

            sheets.Add((
                subnet.Label,
                new TopologyGraph
                {
                    Nodes = nodes,
                    Links = [.. Links.Where(l => attached.Contains(l.From) && attached.Contains(l.To))],
                    Caveats = Caveats,
                }));
        }

        return sheets;

        TopologyNodeKind _nodeKind(string id) =>
            Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal))?.Kind
            ?? TopologyNodeKind.Host;
    }

    public static TopologyGraph Build(TopologyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new Builder(input);

        builder.AddThisMachine();
        builder.AddSubnets();

        // Коммутаторы добавляются до устройств: узел, у которого нашёлся порт
        // коммутатора, цепляется к нему, а не к подсети, и коммутатор к этому
        // моменту уже должен быть на карте.
        builder.AddSwitches();
        builder.AddHeardNeighbors();
        builder.AddDevices();
        builder.AddPaths();
        builder.ApplyEdits();

        return builder.Finish();
    }

    /// <summary>
    /// Сборка карты.
    /// </summary>
    /// <remarks>
    /// Отдельный тип, а не набор статических методов: у построения есть состояние —
    /// уже добавленные узлы и связи, — и протаскивать его параметрами значило бы
    /// сделать каждый шаг длиннее самого правила, которое он выражает.
    /// </remarks>
    private sealed class Builder(TopologyInput input)
    {
        private readonly Dictionary<string, TopologyNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<TopologyLink> _links = [];

        /// <summary>В каких трассировках встретился внешний узел.</summary>
        private readonly Dictionary<string, List<string>> _seenIn = new(StringComparer.Ordinal);

        /// <summary>Связи, которые оператор объявил ошибочными, — в обе стороны.</summary>
        private readonly HashSet<(string From, string To)> _removed = [];

        /// <summary>MAC-адрес → порт коммутатора, в который он воткнут, и его VLAN.</summary>
        private readonly Dictionary<string, (string SwitchId, string Port, int? Vlan)> _wired =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly TopologyInput _input = input;

        public void AddThisMachine()
        {
            var subnet = _input.Subnets.Count > 0 ? _input.Subnets[0] : null;

            Add(new TopologyNode
            {
                Id = ThisMachineId,
                Kind = TopologyNodeKind.ThisMachine,
                Label = "эта машина",
                Address = subnet?.InterfaceAddress,
                Detail = subnet is null ? null : $"интерфейс {subnet.InterfaceName}",
            });
        }

        public void AddSubnets()
        {
            foreach (var subnet in _input.Subnets.OrderBy(s => s.Cidr, StringComparer.Ordinal))
            {
                var id = SubnetId(subnet.Cidr);

                Add(new TopologyNode
                {
                    Id = id,
                    Kind = TopologyNodeKind.Subnet,
                    Label = subnet.Cidr,
                    Detail = subnet.IsVirtual
                        ? $"{subnet.InterfaceName} — виртуальный коммутатор или VPN"
                        : subnet.InterfaceName,
                });

                // Собственный интерфейс — единственная связь, в которой сомневаться
                // не приходится: мы сами в этой сети стоим.
                _links.Add(new TopologyLink(
                    ThisMachineId,
                    id,
                    LinkKind.Layer2,
                    LinkConfidence.Confirmed,
                    $"интерфейс {subnet.InterfaceName} имеет адрес в этой сети"));

                foreach (var gateway in subnet.Gateways)
                {
                    AddGateway(gateway, id);
                }
            }
        }

        private void AddGateway(string address, string subnetId)
        {
            var device = FindDevice(address);
            var id = device?.Identity ?? address;

            Add(new TopologyNode
            {
                Id = id,
                Kind = TopologyNodeKind.Router,
                Label = device?.HostName ?? address,
                Address = address,
                MacAddress = device?.MacAddress,
                Vendor = device?.VendorDisplay,
                Role = device?.RoleDisplay,
                IsOnline = device?.IsOnline ?? true,
                Detail = "шлюз по умолчанию",
            });

            _links.Add(new TopologyLink(
                subnetId,
                id,
                LinkKind.Layer2,
                LinkConfidence.Confirmed,
                "шлюз стоит в этой сети"));

            // Что шлюз ведёт наружу — вывод из его роли, а не наблюдение. Обычно верный,
            // но сеть без выхода в интернет существует, и утверждать обратное нельзя.
            _links.Add(new TopologyLink(
                id,
                InternetId,
                LinkKind.Routed,
                LinkConfidence.Inferred,
                "маршрут по умолчанию ведёт через этот узел"));

            EnsureInternet();
        }

        /// <summary>
        /// Коммутаторы, опрошенные по SNMP.
        /// </summary>
        /// <remarks>
        /// Здесь карта перестаёт быть догадкой. Ответ на ARP говорит «в одном
        /// широковещательном домене»; таблица пересылки коммутатора говорит
        /// «в порту Gi0/2», и это разные утверждения по силе.
        /// </remarks>
        public void AddSwitches()
        {
            foreach (var device in _input.Switches.OrderBy(d => d.Address, StringComparer.Ordinal))
            {
                AddSwitch(device);
            }

            // Соседи добавляются после всех коммутаторов: связь между двумя опрошенными
            // устройствами должна соединять их узлы, а не плодить двойников по имени.
            foreach (var device in _input.Switches.OrderBy(d => d.Address, StringComparer.Ordinal))
            {
                AddNeighbors(device);
            }
        }

        private void AddSwitch(SnmpDevice device)
        {
            var id = SwitchId(device.Address);

            Add(new TopologyNode
            {
                Id = id,
                Kind = TopologyNodeKind.Switch,
                Label = device.DisplayName,
                Address = device.Address,
                Detail = $"{device.System.ShortDescription}; работает {device.System.DescribeUpTime()}",
            });

            foreach (var subnet in _input.Subnets)
            {
                if (InSubnet(device.Address, subnet))
                {
                    _links.Add(new TopologyLink(
                        SubnetId(subnet.Cidr),
                        id,
                        LinkKind.Layer2,
                        LinkConfidence.Confirmed,
                        "коммутатор отвечает по SNMP с адресом в этой сети"));
                }
            }

            RememberPorts(device, id);
        }

        /// <summary>
        /// Запоминает, какой адрес в каком порту.
        /// </summary>
        /// <remarks>
        /// Берутся только порты с <b>ровно одним</b> выученным адресом. Порт, за которым
        /// видно десять адресов, ведёт не к десяти компьютерам, а к следующему
        /// коммутатору, и цеплять к нему устройства поимённо значило бы нарисовать
        /// заведомо неверную картину.
        /// </remarks>
        private void RememberPorts(SnmpDevice device, string switchId)
        {
            foreach (var port in device.Ports())
            {
                if (port.Neighbors.Count > 0 || port.SoleAddress is not { } mac || !port.Interface.IsPhysical)
                {
                    continue;
                }

                // Номер VLAN берётся из той же записи таблицы пересылки, что и адрес:
                // он есть, только если таблица читалась через Q-BRIDGE-MIB.
                _wired[mac] = (switchId, port.Interface.DisplayName, port.Addresses[0].Vlan);
            }
        }

        /// <summary>
        /// Соседи, объявленные самим устройством.
        /// </summary>
        /// <remarks>
        /// Самое сильное свидетельство о втором уровне: устройство называет и свой порт,
        /// и порт соседа. Оговорка в строке «почему» обязательна — между двумя
        /// объявившимися соседями может стоять неуправляемый коммутатор.
        /// </remarks>
        private void AddNeighbors(SnmpDevice device)
        {
            var from = SwitchId(device.Address);

            foreach (var neighbor in device.Neighbors.OrderBy(n => n.LocalIfIndex))
            {
                var to = FindNeighbor(neighbor) ?? SwitchId(neighbor.DisplayName);

                if (!_nodes.ContainsKey(to))
                {
                    Add(new TopologyNode
                    {
                        Id = to,
                        Kind = TopologyNodeKind.Switch,
                        Label = neighbor.DisplayName,
                        Address = neighbor.RemoteAddress,
                        MacAddress = neighbor.RemoteChassisId,
                        Detail = neighbor.RemoteDescription is { } about
                            ? $"{about}; объявлен соседом, сам не опрошен"
                            : "объявлен соседом, сам не опрошен",
                    });
                }

                if (!string.Equals(from, to, StringComparison.Ordinal))
                {
                    _links.Add(new TopologyLink(from, to, LinkKind.Layer2, LinkConfidence.Confirmed,
                        neighbor.Because + " — между ними может стоять неуправляемый коммутатор"));
                }
            }
        }

        /// <summary>Узел соседа среди уже опрошенных: по имени или по адресу управления.</summary>
        private string? FindNeighbor(LinkNeighbor neighbor)
        {
            foreach (var known in _input.Switches)
            {
                var matches =
                    (neighbor.RemoteAddress is { } address
                     && string.Equals(known.Address, address, StringComparison.Ordinal))
                    || (neighbor.RemoteName is { } name
                        && string.Equals(known.System.Name, name, StringComparison.OrdinalIgnoreCase));

                if (matches)
                {
                    return SwitchId(known.Address);
                }
            }

            return null;
        }

        /// <summary>
        /// Соседи, услышанные своим адаптером.
        /// </summary>
        /// <remarks>
        /// Связь идёт от <b>этой машины</b>, а не от подсети: кадр пришёл к нам,
        /// и он утверждает ровно одно — вот к этому устройству и вот в этот его порт
        /// мы подключены. Это единственный источник, который отвечает на такой вопрос
        /// без учётных данных к оборудованию.
        /// </remarks>
        public void AddHeardNeighbors()
        {
            foreach (var neighbor in _input.Neighbors
                         .OrderBy(n => n.DisplayName, StringComparer.CurrentCulture)
                         .ThenBy(n => n.RemotePort, StringComparer.Ordinal))
            {
                var id = FindNeighbor(neighbor) ?? SwitchId(neighbor.DisplayName);

                if (!_nodes.ContainsKey(id))
                {
                    Add(new TopologyNode
                    {
                        Id = id,
                        Kind = TopologyNodeKind.Switch,
                        Label = neighbor.DisplayName,
                        Address = neighbor.RemoteAddress,
                        MacAddress = neighbor.RemoteChassisId,
                        Detail = neighbor.RemoteDescription is { } about
                            ? $"{about}; услышан своим адаптером"
                            : "услышан своим адаптером, сам не опрошен",
                    });
                }

                _links.Add(new TopologyLink(
                    ThisMachineId,
                    id,
                    LinkKind.Layer2,
                    LinkConfidence.Confirmed,
                    neighbor.Because + " — этим проводом подключены мы сами"));
            }
        }

        public void AddDevices()
        {
            foreach (var subnet in _input.Subnets)
            {
                AddDevicesOf(subnet);
            }
        }

        private void AddDevicesOf(LocalSubnet subnet)
        {
            var subnetId = SubnetId(subnet.Cidr);
            var expanded = _input.ExpandedSubnets.Contains(subnet.Cidr, StringComparer.Ordinal);

            // Порядок фиксирован: карта обязана получаться одинаковой при каждом
            // пересчёте, иначе «что изменилось» покажет перестановку вместо изменений.
            //
            // Устройства, которых касались правки оператора, идут первыми и в свёртку
            // не попадают. Если человек нарисовал к узлу связь, он этим узлом занят —
            // спрятать его в счётчик значило бы стереть его же работу.
            var members = _input.Devices
                .Where(d => !_nodes.ContainsKey(d.Identity) && BelongsTo(d, subnet))
                .OrderByDescending(d => IsEdited(d))
                .ThenBy(d => IpAddressOrder.Of(d.Address))
                .ThenBy(d => d.Identity, StringComparer.Ordinal)
                .ToList();

            var pinned = members.Count(IsEdited);
            var shown = expanded ? members.Count : Math.Max(pinned, Math.Min(members.Count, _input.CollapseThreshold));

            for (var i = 0; i < shown; i++)
            {
                AddMember(members[i], subnetId);
            }

            if (shown >= members.Count)
            {
                return;
            }

            // Остальные сворачиваются в счётчик. Триста прямоугольников с адресами —
            // не карта, а список: порог и его причина в спайке-04.
            var hidden = members.Count - shown;

            Add(new TopologyNode
            {
                Id = $"{subnetId}/остальные",
                Kind = TopologyNodeKind.HostGroup,
                Label = $"ещё {hidden.ToString(CultureInfo.InvariantCulture)} "
                        + Text.Plural.Of(hidden, "устройство", "устройства", "устройств"),
                GroupSize = hidden,
                Detail = "разверните, чтобы увидеть поимённо",
                IsOnline = members.Skip(shown).Any(d => d.IsOnline),
            });

            _links.Add(new TopologyLink(
                subnetId,
                $"{subnetId}/остальные",
                LinkKind.Layer2,
                LinkConfidence.Confirmed,
                $"{hidden.ToString(CultureInfo.InvariantCulture)} устройств этой сети свёрнуты"));
        }

        private void AddMember(Device device, string subnetId)
        {
            Add(new TopologyNode
            {
                Id = device.Identity,
                Kind = TopologyNodeKind.Host,
                Label = device.DisplayName,
                Address = device.Address,
                MacAddress = device.MacAddress,
                Vendor = device.VendorDisplay,
                Role = device.RoleDisplay,
                IsOnline = device.IsOnline,
                Detail = device.ExtraAddresses.Count > 0
                    ? "ещё адреса: " + string.Join(", ", device.ExtraAddresses)
                    : null,
                Vlan = device.MacAddress is { } known && _wired.TryGetValue(known, out var seen)
                    ? seen.Vlan
                    : null,
            });

            // Порт коммутатора весит больше принадлежности подсети: он называет
            // не «где-то в этом домене», а «вот в этом гнезде».
            if (device.MacAddress is { } mac && _wired.TryGetValue(mac, out var wired))
            {
                // VLAN дописывается в причину, а не только в поле узла: строку «почему»
                // читают, когда связь вызывает сомнение, и номер домена — первое,
                // что там нужно.
                var vlan = wired.Vlan is { } number
                    ? $", VLAN {number.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;

                _links.Add(new TopologyLink(
                    wired.SwitchId,
                    device.Identity,
                    LinkKind.Layer2,
                    LinkConfidence.Confirmed,
                    $"порт {wired.Port}: адрес выучен таблицей пересылки коммутатора (BRIDGE-MIB){vlan}"));

                return;
            }

            var (confidence, because) = Adjacency(device);

            _links.Add(new TopologyLink(subnetId, device.Identity, LinkKind.Layer2, confidence, because));
        }

        /// <summary>
        /// Насколько уверенно устройство принадлежит широковещательному домену.
        /// </summary>
        /// <remarks>
        /// Ответ на ARP — это доказательство: протокол работает только внутри одного
        /// домена, и раз узел ответил, он там. Ответ на ICMP или TCP таким
        /// доказательством не является: пакет мог пройти через маршрутизатор, а адрес
        /// из диапазона подсети ещё ничего не гарантирует.
        /// </remarks>
        private static (LinkConfidence Confidence, string Because) Adjacency(Device device)
        {
            var arp = device.Evidence.Any(e =>
                e.Kind == EvidenceKind.MacAddress
                && e.Source is EvidenceSource.ArpTable or EvidenceSource.ArpRequest);

            return arp
                ? (LinkConfidence.Confirmed, "узел ответил на ARP — значит в одном широковещательном домене")
                : (LinkConfidence.Assumed, "адрес попадает в диапазон подсети, но ответа на ARP не было");
        }

        public void AddPaths()
        {
            foreach (var path in _input.Paths.OrderBy(p => p.Destination, StringComparer.Ordinal))
            {
                AddPath(path);
            }
        }

        /// <summary>
        /// Куда цеплять начало пути: к шлюзу, если он известен, иначе к своей сети.
        /// </summary>
        /// <remarks>
        /// Без этой связи цепочка трассировки повисает в стороне от карты: первый
        /// ответивший хоп обычно уже за шлюзом, и никакая другая связь его не касается.
        /// </remarks>
        private string? PathAnchor()
        {
            foreach (var subnet in _input.Subnets)
            {
                foreach (var gateway in subnet.Gateways)
                {
                    var device = FindDevice(gateway);
                    var id = device?.Identity ?? gateway;

                    if (_nodes.ContainsKey(id))
                    {
                        return id;
                    }
                }
            }

            var first = _input.Subnets.Count > 0 ? SubnetId(_input.Subnets[0].Cidr) : null;

            if (first is not null && _nodes.ContainsKey(first))
            {
                return first;
            }

            // Ни шлюза, ни своей сети — так бывает, когда все интерфейсы отфильтрованы
            // или у машины нет адреса IPv4. Путь всё равно измерен отсюда, и повесить
            // его на саму машину честнее, чем оставить цепочку висеть в пустоте.
            return ThisMachineId;
        }

        private void AddPath(PathObservation path)
        {
            string? previous = null;
            var anchor = PathAnchor();

            foreach (var hop in path.Hops)
            {
                var device = FindDevice(hop);
                var id = device?.Identity ?? hop;

                if (_nodes.TryGetValue(id, out var known) && known.Kind == TopologyNodeKind.ExternalHop)
                {
                    // Узел встречается в нескольких трассировках — общий участок пути.
                    // Назвать только первую значило бы скрыть, что через него идёт
                    // не одно направление, а несколько.
                    _seenIn.TryGetValue(id, out var targets);
                    targets ??= [];

                    if (!targets.Contains(path.Destination, StringComparer.Ordinal))
                    {
                        targets.Add(path.Destination);
                        _seenIn[id] = targets;

                        _nodes[id] = known with
                        {
                            Detail = "встречен в трассировках до " + string.Join(", ", targets),
                        };
                    }
                }
                else if (!_nodes.ContainsKey(id))
                {
                    _seenIn[id] = [path.Destination];

                    Add(new TopologyNode
                    {
                        Id = id,
                        Kind = TopologyNodeKind.ExternalHop,
                        Label = device?.HostName ?? hop,
                        Address = hop,
                        Vendor = device?.VendorDisplay,
                        Detail = $"встречен в трассировке до {path.Destination}",
                    });
                }

                if (previous is null && anchor is not null && anchor != id)
                {
                    // Первый ответивший хоп трассировки. Соседом шлюза он быть не обязан:
                    // хопы перед ним могли промолчать, а туннель мог скрыть целый участок.
                    _links.Add(new TopologyLink(
                        anchor,
                        id,
                        LinkKind.Path,
                        LinkConfidence.Inferred,
                        $"первый ответивший узел на пути до {path.Destination}; "
                        + "между ним и шлюзом могут быть узлы, не ответившие на трассировку"));
                }

                if (previous is not null && previous != id)
                {
                    // Соседние хопы трассировки не обязаны быть соседями в сети:
                    // туннель MPLS без переноса TTL прячет целые участки пути.
                    // Это выяснилось в И-7 — цель отвечала сразу с нескольких TTL.
                    _links.Add(new TopologyLink(
                        previous,
                        id,
                        LinkKind.Path,
                        LinkConfidence.Inferred,
                        path.HasGaps
                            ? "соседние хопы трассировки; в пути были молчащие узлы, "
                              + "между этими двумя может быть ещё несколько"
                            : "соседние хопы трассировки; туннели могут скрывать промежуточные узлы"));
                }

                previous = id;
            }

            if (previous is not null)
            {
                // Конечная точка пути смотрит наружу: дальше начинается то,
                // о чём мы ничего не знаем.
                EnsureInternet();

                _links.Add(new TopologyLink(
                    previous,
                    InternetId,
                    LinkKind.Routed,
                    LinkConfidence.Inferred,
                    $"конечная точка трассировки до {path.Destination}"));
            }
        }

        /// <summary>
        /// Накладывает правки оператора поверх наблюдений.
        /// </summary>
        /// <remarks>
        /// Порядок обязателен: правки идут последними и перекрывают всё, что вывел
        /// инструмент. Человек, который видел провод, знает больше любой эвристики —
        /// и его связь помечается подтверждённой, а не выведенной.
        /// <para>
        /// Скрытые узлы удаляются вместе со своими связями: узел, которого нет,
        /// не может быть ни к чему подключён.
        /// </para>
        /// </remarks>
        public void ApplyEdits()
        {
            foreach (var edit in _input.Edits.OrderBy(e => e.AtUtc).ThenBy(e => e.Id))
            {
                switch (edit.Kind)
                {
                    case TopologyEditKind.AddLink when edit.Target is { } target:
                        AddManualLink(edit, target);
                        break;

                    case TopologyEditKind.RemoveLink when edit.Target is { } target:
                        Forbid(edit.Subject, target);
                        break;

                    case TopologyEditKind.HideNode:
                        if (FindNode(edit.Subject) is { } hidden)
                        {
                            _nodes.Remove(hidden);
                        }

                        break;

                    default:
                        break;
                }
            }
        }

        /// <summary>Запрещает связь в обе стороны: направление рисования не должно решать.</summary>
        private void Forbid(string subject, string target)
        {
            var from = FindNode(subject) ?? subject;
            var to = FindNode(target) ?? target;

            _removed.Add((from, to));
            _removed.Add((to, from));
        }

        private void AddManualLink(TopologyEdit edit, string target)
        {
            var from = FindNode(edit.Subject);
            var to = FindNode(target);

            if (from is null || to is null)
            {
                // Связь к несуществующему узлу молча пропускается: устройство могло
                // исчезнуть из сети после того, как оператор её нарисовал. Сама правка
                // при этом остаётся — вернётся устройство, вернётся и связь.
                return;
            }

            _links.Add(new TopologyLink(
                from,
                to,
                LinkKind.Layer2,
                LinkConfidence.Confirmed,
                edit.Note is { Length: > 0 } note
                    ? $"связь указана оператором: {note}"
                    : "связь указана оператором"));
        }

        /// <summary>Находит узел по тождеству или по адресу — оператор называет и так, и так.</summary>
        private string? FindNode(string reference)
        {
            if (_nodes.ContainsKey(reference))
            {
                return reference;
            }

            foreach (var node in _nodes.Values)
            {
                if (string.Equals(node.Address, reference, StringComparison.Ordinal))
                {
                    return node.Id;
                }
            }

            return null;
        }

        private void EnsureInternet() => Add(new TopologyNode
        {
            Id = InternetId,
            Kind = TopologyNodeKind.Internet,
            Label = "интернет",
            Detail = "всё, что за пределами известных нам сетей",
        });

        public TopologyGraph Finish()
        {
            // Дубли связей возможны: один узел бывает и шлюзом, и хопом трассировки.
            // Из совпавших остаётся самая уверенная — правило, а не порядок добавления.
            var best = new Dictionary<(string, string, LinkKind), TopologyLink>();

            foreach (var link in _links)
            {
                if (!_nodes.ContainsKey(link.From) || !_nodes.ContainsKey(link.To))
                {
                    continue;
                }

                // Связь, объявленную оператором ошибочной, не рисуем — сколько бы
                // наблюдений её ни подтверждало.
                if (_removed.Contains((link.From, link.To)))
                {
                    continue;
                }

                var key = (link.From, link.To, link.Kind);

                if (!best.TryGetValue(key, out var existing) || link.Confidence < existing.Confidence)
                {
                    best[key] = link;
                }
            }

            return new TopologyGraph
            {
                Nodes = [.. _nodes.Values
                    .OrderBy(n => (int)n.Kind)
                    .ThenBy(n => IpAddressOrder.Of(n.Address))
                    .ThenBy(n => n.Id, StringComparer.Ordinal)],
                Links = [.. best.Values
                    .OrderBy(l => l.From, StringComparer.Ordinal)
                    .ThenBy(l => l.To, StringComparer.Ordinal)
                    .ThenBy(l => (int)l.Kind)],
                Caveats = VlanCaveats(),
            };
        }

        /// <summary>
        /// Оговорки про VLAN.
        /// </summary>
        /// <remarks>
        /// Карта показывает физическую структуру: устройства висят на портах того
        /// коммутатора, в который воткнуты. Это верно и в сети с VLAN — но читается там
        /// неверно, потому что разные VLAN на одном коммутаторе суть разные
        /// широковещательные домены, и устройства в них друг друга не видят.
        /// <para>
        /// Оговорка появляется, только когда VLAN <b>известны и различаются</b>.
        /// На коммутаторе без Q-BRIDGE-MIB номера нет вовсе, и молчать здесь честнее,
        /// чем предупреждать о том, чего не наблюдали.
        /// </para>
        /// </remarks>
        private List<string> VlanCaveats()
        {
            var caveats = new List<string>();

            var bySwitch = _wired.Values
                .Where(w => w.Vlan is not null)
                .GroupBy(w => w.SwitchId, StringComparer.Ordinal);

            foreach (var group in bySwitch.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var vlans = group
                    .Select(w => w.Vlan!.Value)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();

                if (vlans.Count < 2)
                {
                    continue;
                }

                var label = _nodes.TryGetValue(group.Key, out var node) ? node.Label : group.Key;

                caveats.Add(
                    $"На «{label}» устройства в разных VLAN: "
                    + string.Join(", ", vlans.Select(v => v.ToString(CultureInfo.InvariantCulture)))
                    + ". Они висят на одном узле карты, но соседями не являются: разные VLAN — "
                    + "разные широковещательные домены, и друг друга эти устройства не видят.");
            }

            return caveats;
        }

        private void Add(TopologyNode node) => _nodes.TryAdd(node.Id, node);

        private Device? FindDevice(string address) =>
            _input.Devices.FirstOrDefault(d =>
                string.Equals(d.Address, address, StringComparison.Ordinal)
                || d.Addresses.Contains(address, StringComparer.Ordinal));

        private static bool BelongsTo(Device device, LocalSubnet subnet)
        {
            AddressRange range;

            try
            {
                range = AddressRange.Parse(subnet.Cidr);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }

            var addresses = device.Addresses.Count > 0 ? device.Addresses : [device.Address];

            return addresses.Any(a => System.Net.IPAddress.TryParse(a, out var parsed) && range.Contains(parsed));
        }

        /// <summary>Касались ли этого устройства правки оператора.</summary>
        private bool IsEdited(Device device)
        {
            foreach (var edit in _input.Edits)
            {
                if (Mentions(edit, device))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Mentions(TopologyEdit edit, Device device)
        {
            if (Matches(edit.Subject, device) || (edit.Target is { } target && Matches(target, device)))
            {
                return edit.Kind != TopologyEditKind.HideNode;
            }

            return false;
        }

        /// <summary>
        /// Правка может называть устройство и тождеством, и адресом.
        /// </summary>
        /// <remarks>
        /// Оператор набирает то, что видит на экране, а видит он чаще адрес. Требовать
        /// от него MAC значило бы сделать правку неудобной ровно там, где она нужна.
        /// </remarks>
        private static bool Matches(string reference, Device device) =>
            string.Equals(reference, device.Identity, StringComparison.OrdinalIgnoreCase)
            || device.Addresses.Contains(reference, StringComparer.Ordinal)
            || string.Equals(reference, device.Address, StringComparison.Ordinal);

        private static string SubnetId(string cidr) => "сеть:" + cidr;

        private static string SwitchId(string address) => "свитч:" + address;

        private static bool InSubnet(string address, LocalSubnet subnet)
        {
            try
            {
                return System.Net.IPAddress.TryParse(address, out var parsed)
                       && AddressRange.Parse(subnet.Cidr).Contains(parsed);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }
        }
    }

}
