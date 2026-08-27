using System.Globalization;
using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.Topology;

/// <summary>Что за узел на карте.</summary>
public enum TopologyNodeKind
{
    /// <summary>Машина, с которой ведутся измерения.</summary>
    ThisMachine,

    /// <summary>Широковещательный домен — то, что мы честно можем утверждать про L2.</summary>
    Subnet,

    Router,

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

    /// <summary>Сколько устройств свёрнуто в этот узел. 0 — узел не свёрнутый.</summary>
    public int GroupSize { get; init; }

    public bool IsOnline { get; init; } = true;

    /// <summary>Строка подробностей для подсказки.</summary>
    public string? Detail { get; init; }
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

    public bool IsEmpty => Nodes.Count == 0;

    public int ConfirmedLinks => Links.Count(l => l.Confidence == LinkConfidence.Confirmed);

    public int InferredLinks => Links.Count(l => l.Confidence != LinkConfidence.Confirmed);

    public static TopologyGraph Empty { get; } = new() { Nodes = [], Links = [] };

    public static TopologyGraph Build(TopologyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new Builder(input);

        builder.AddThisMachine();
        builder.AddSubnets();
        builder.AddDevices();
        builder.AddPaths();

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
            var members = _input.Devices
                .Where(d => !_nodes.ContainsKey(d.Identity) && BelongsTo(d, subnet))
                .OrderBy(d => AddressOrder(d.Address))
                .ThenBy(d => d.Identity, StringComparer.Ordinal)
                .ToList();

            var shown = expanded ? members.Count : Math.Min(members.Count, _input.CollapseThreshold);

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
                        + Plural(hidden, "устройство", "устройства", "устройств"),
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
                IsOnline = device.IsOnline,
                Detail = device.ExtraAddresses.Count > 0
                    ? "ещё адреса: " + string.Join(", ", device.ExtraAddresses)
                    : null,
            });

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
                    .ThenBy(n => AddressOrder(n.Address ?? string.Empty))
                    .ThenBy(n => n.Id, StringComparer.Ordinal)],
                Links = [.. best.Values
                    .OrderBy(l => l.From, StringComparer.Ordinal)
                    .ThenBy(l => l.To, StringComparer.Ordinal)
                    .ThenBy(l => (int)l.Kind)],
            };
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

        private static string SubnetId(string cidr) => "сеть:" + cidr;
    }

    /// <summary>Числовой порядок адреса — чтобы узлы шли как в сети, а не как в словаре.</summary>
    private static uint AddressOrder(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return uint.MaxValue;
        }

        var bytes = parsed.GetAddressBytes();

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var tens = count % 100;

        if (tens is >= 11 and <= 14)
        {
            return many;
        }

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
