using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace StormMachine.Protocol;

/// <summary>
/// Кто объявил о себе в локальной сети: агент или клиент.
/// </summary>
/// <remarks>
/// Объявляются обе стороны, и запись одна на обе. Раньше она называлась «агентом»,
/// потому что объявлялся только он: клиент искал агента, а обратно — никак, и на чужой
/// площадке адрес машины оператора приходилось узнавать и набирать руками. Направление
/// связи выбирают по тому, где заблокированы входящие, а не по тому, кто кого умеет
/// найти, — значит, находить должны оба.
/// </remarks>
public sealed record AnnouncedPeer
{
    public required string Address { get; init; }

    public required int Port { get; init; }

    public required string MachineName { get; init; }

    /// <summary>Чем объявился: <c>storm-agent/версия</c> или <c>storm/версия</c>.</summary>
    public string? Product { get; init; }

    /// <summary>Начало отпечатка: сверить с полным при сопряжении.</summary>
    public string? ThumbprintPrefix { get; init; }

    /// <summary>Это агент — удалённая точка измерения.</summary>
    public bool IsAgent => Product?.StartsWith(LocalDiscovery.AgentProduct, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Это клиент — машина оператора, ждущая звонка агента.</summary>
    public bool IsClient => !IsAgent
                            && Product?.StartsWith(LocalDiscovery.ClientProduct, StringComparison.OrdinalIgnoreCase) == true;

    public string Describe() =>
        $"{MachineName} на {Address}:{Port}"
        + (Product is { Length: > 0 } product ? $" ({product})" : string.Empty);
}

/// <summary>
/// Обнаружение агентов в локальной сети через mDNS.
/// </summary>
/// <remarks>
/// Избавляет от набора адреса — и только от этого. Сопряжение всё равно требует кода
/// и сверки отпечатка: объявление в сети говорит лишь «здесь кто-то есть», а кто именно,
/// подтверждает отпечаток. Доверять объявлению нельзя, подделать его может кто угодно.
/// <para>
/// Работает в пределах одной подсети: mDNS не маршрутизируется. Агент на удалённой
/// площадке так не найдётся никогда, и обещать обратное было бы враньём — отсюда
/// и название «локальное».
/// </para>
/// <para>
/// Ответы приходят входящим трафиком, а он на Windows заблокирован по умолчанию
/// (спайк-05). Пустой результат поэтому не означает «агентов нет» и так и сообщается.
/// </para>
/// </remarks>
public static class LocalDiscovery
{
    /// <summary>Чем объявляется агент — удалённая точка измерения.</summary>
    public const string AgentProduct = "storm-agent";

    /// <summary>Чем объявляется клиент — машина оператора.</summary>
    /// <remarks>
    /// Клиент объявляется только пока ждёт звонка агента, а не всё время работы.
    /// Станция диагностики, постоянно кричащая о себе в чужой сети, — это не то,
    /// чем ей следует быть: объявление оправдано ровно тогда, когда его ждут.
    /// </remarks>
    public const string ClientProduct = "storm";

    /// <summary>Имя службы в DNS-SD.</summary>
    public const string ServiceName = "_storm._tcp.local";

    private const int MulticastPort = 5353;

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");

    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort TypeA = 1;

    /// <summary>
    /// Объявляет себя, пока не отменят.
    /// </summary>
    /// <remarks>
    /// Объявление повторяется, а не делается однократно: клиент мог запуститься позже,
    /// а слушать эфир бесконечно ради одного пропущенного пакета он не станет.
    /// </remarks>
    public static async Task AnnounceAsync(
        string machineName,
        int port,
        string product,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        // Объявляемся на КАЖДОЙ пригодной карте, а не на одной.
        //
        // Раньше адрес и маршрут выбирала система: сокет «подключался» к групповому
        // адресу, и бралось то, что она вернёт. На машине с туннелем она вернула
        // 198.18.0.1 — адрес, по которому до нас не достучится никто, и объявление
        // ушло в туннель вместо локальной сети. Со стороны это выглядело как
        // «связь не работает»: обнаружение формально работало, а звонить было некуда.
        //
        // Поэтому каждая карта объявляет СВОЙ адрес и шлёт со своего интерфейса:
        // услышавший по этому адресу и дозвонится.
        var cards = Cards();

        if (cards.Count == 0)
        {
            return;
        }

        var sockets = new List<(Socket Socket, byte[] Packet)>(cards.Count);

        try
        {
            foreach (var card in cards)
            {
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(card, 0));

                    // Исходящий интерфейс групповой рассылки — тот же, чей адрес
                    // объявлен: иначе пакет уйдёт не туда, куда зовёт.
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        card.GetAddressBytes());

                    sockets.Add((socket, BuildAnnouncement(machineName, port, product, thumbprint, card)));
                }
                catch (SocketException)
                {
                    // Карта не годится для групповой рассылки — остальные годятся.
                }
            }

            var target = new IPEndPoint(MulticastGroup, MulticastPort);

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var (socket, packet) in sockets)
                {
                    try
                    {
                        await socket.SendToAsync(packet, SocketFlags.None, target, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Эта карта замолчала — остальные продолжают.
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановлено — объявлять больше нечего.
        }
        finally
        {
            foreach (var (socket, _) in sockets)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Ближе ли новый адрес того же собеседника, чем уже известный.
    /// </summary>
    /// <remarks>
    /// Ближе — значит в одной сети с какой-нибудь нашей картой: по такому адресу
    /// пакет дойдёт напрямую. Прочие адреса той же машины (внутренний коммутатор,
    /// туннель) мы слышим, но дозвониться по ним не можем.
    /// </remarks>
    /// <summary>
    /// Насколько адрес «свой»: 0 — сеть маршрута по умолчанию, 1 — какая-то наша сеть, 2 — чужой.
    /// </summary>
    /// <remarks>
    /// Сеть маршрута по умолчанию — та, через которую машина и работает. Прочие наши
    /// сети (внутренний коммутатор Hyper-V, туннель) тоже «свои», но дозвониться
    /// по ним можно не всегда и не отовсюду. Разница видна ровно там, где она важна:
    /// одна и та же машина объявляется со всех карт, и услышать её можно с нескольких
    /// сразу.
    /// </remarks>
    private static int Distance(string address)
    {
        if (!IPAddress.TryParse(address, out var peer) || peer.AddressFamily != AddressFamily.InterNetwork)
        {
            return 2;
        }

        var distance = 2;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var routed = properties.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                              && !g.Address.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || !SameNetwork(unicast.Address, peer, unicast.PrefixLength))
                    {
                        continue;
                    }

                    if (routed)
                    {
                        return 0;
                    }

                    distance = Math.Min(distance, 1);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Список карт не прочитался — считаем адрес обычным.
        }

        return distance;
    }

    /// <summary>Лежат ли два адреса в одной сети по длине префикса.</summary>
    private static bool SameNetwork(IPAddress ours, IPAddress theirs, int prefix)
    {
        if (prefix is <= 0 or > 32)
        {
            return false;
        }

        var a = BinaryPrimitives.ReadUInt32BigEndian(ours.GetAddressBytes());
        var b = BinaryPrimitives.ReadUInt32BigEndian(theirs.GetAddressBytes());
        var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);

        return (a & mask) == (b & mask);
    }

    /// <summary>
    /// Карты, на которых имеет смысл объявляться и слушать.
    /// </summary>
    /// <remarks>
    /// Поднятые, с адресом IPv4, умеющие групповую рассылку и не петлевые. Туннели
    /// и виртуальные коммутаторы при этом не выбрасываются: за виртуальным
    /// коммутатором Hyper-V живёт вся сеть машины, и выкинуть его значило бы
    /// не объявляться вовсе на самой обычной рабочей машине.
    /// </remarks>
    private static List<IPAddress> Cards()
    {
        var found = new List<IPAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || !nic.SupportsMulticast)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        found.Add(unicast.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Список карт не прочитался — пусть решает система, как раньше.
        }

        if (found.Count == 0 && LocalAddress() is { } fallback && !Equals(fallback, IPAddress.Any))
        {
            found.Add(fallback);
        }

        return found;
    }

    /// <summary>Слушает объявления заданное время.</summary>
    public static async Task<IReadOnlyList<AnnouncedPeer>> BrowseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        var found = new Dictionary<string, AnnouncedPeer>(StringComparer.OrdinalIgnoreCase);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            // Вступаем в группу на каждой карте, а не на той, что выберет система.
            // Объявление приходит с той карты, где его послали, и слушать одну
            // значит не услышать соседа, подключённого в другую сеть.
            var joined = 0;

            foreach (var card in Cards())
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MulticastGroup, card));

                    joined++;
                }
                catch (SocketException)
                {
                    // На этой карте группа недоступна — остальные годятся.
                }
            }

            if (joined == 0)
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(MulticastGroup, IPAddress.Any));
            }
        }
        catch (SocketException ex)
        {
            throw new ProtocolException(
                $"Слушать объявления не удалось: {ex.SocketErrorCode}. "
                + "Порт 5353 может быть занят службой Bonjour или другой программой.",
                ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);

        var buffer = new byte[4096];
        var any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, any, deadline.Token)
                    .ConfigureAwait(false);

                if (TryReadAnnouncement(buffer.AsSpan(0, received.ReceivedBytes),
                        (IPEndPoint)received.RemoteEndPoint, out var peer))
                {
                    // Одна машина объявляется со всех своих карт, и слышно её может
                    // быть с нескольких сразу. Раньше оставался последний услышанный
                    // адрес — на машине с туннелем это оказывался адрес туннеля,
                    // по которому до неё не достучаться.
                    //
                    // Выбирать за человека нельзя: на машине и у туннеля, и у сети
                    // бывает шлюз, и какой из адресов «настоящий» — вопрос к тому,
                    // кто знает свою сеть. Поэтому слышимые адреса не затирают друг
                    // друга, а показываются все; порядок — от самого вероятного.
                    found[$"{peer.MachineName}|{peer.Address}"] = peer;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Время вышло — возвращаем найденное.
        }
        catch (SocketException)
        {
            // Эфир оборвался. То, что успели услышать, остаётся в силе.
        }

        return
        [
            .. found.Values
                .OrderBy(a => Distance(a.Address))
                .ThenBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Address, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Собирает объявление: PTR, SRV, TXT и A одним пакетом.
    /// </summary>
    /// <remarks>
    /// Без сжатия имён по указателям. Сжатие экономит десятки байт в пакете, который
    /// уходит раз в десять секунд, и стоит самого хитрого места в разборе DNS —
    /// цена и выгода тут несопоставимы.
    /// </remarks>
    internal static byte[] BuildAnnouncement(
        string machineName,
        int port,
        string product,
        string thumbprint,
        IPAddress address)
    {
        var instance = $"{Sanitize(machineName)}.{ServiceName}";
        var host = $"{Sanitize(machineName)}.local";

        var body = new List<byte>(512);

        Append(body, ServiceName, TypePtr, Name(instance));
        Append(body, instance, TypeSrv, Service(port, host));
        Append(body, instance, TypeTxt, Text(product, thumbprint, machineName));
        Append(body, host, TypeA, address.GetAddressBytes());

        var packet = new byte[12 + body.Count];

        // Ответ, а не запрос: объявление — это незапрошенный ответ (RFC 6762 §8.3).
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 4);

        body.CopyTo(packet, 12);

        return packet;
    }

    private static void Append(List<byte> body, string name, ushort type, byte[] data)
    {
        body.AddRange(Name(name));

        var header = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), type);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 120);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)data.Length);

        body.AddRange(header);
        body.AddRange(data);
    }

    private static byte[] Name(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(name.Length + 2);

        foreach (var label in labels)
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)Math.Min(63, encoded.Length));
            bytes.AddRange(encoded.Take(63));
        }

        bytes.Add(0);

        return [.. bytes];
    }

    private static byte[] Service(int port, string host)
    {
        var name = Name(host);
        var data = new byte[6 + name.Length];

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)port);
        name.CopyTo(data, 6);

        return data;
    }

    private static byte[] Text(string product, string thumbprint, string machineName)
    {
        var parts = new[]
        {
            $"product={product}",
            $"machine={machineName}",
            $"thumb={(thumbprint.Length >= 16 ? thumbprint[..16] : thumbprint)}",
        };

        var data = new List<byte>(128);

        foreach (var part in parts)
        {
            var encoded = Encoding.UTF8.GetBytes(part);
            data.Add((byte)Math.Min(255, encoded.Length));
            data.AddRange(encoded.Take(255));
        }

        return [.. data];
    }

    /// <summary>
    /// Читает объявление.
    /// </summary>
    /// <remarks>
    /// Разбирается только то, что нужно: порт из SRV и подписи из TXT. Адрес берётся
    /// из того, откуда пакет пришёл, а не из записи A: запись можно написать любую,
    /// а обратный адрес пакета — тот, с которым придётся разговаривать.
    /// </remarks>
    internal static bool TryReadAnnouncement(
        ReadOnlySpan<byte> packet,
        IPEndPoint? from,
        out AnnouncedPeer peer)
    {
        peer = default!;

        if (packet.Length < 12 || from is null)
        {
            return false;
        }

        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        var offset = 12;

        var port = 0;
        string? product = null;
        string? machine = null;
        string? thumb = null;
        var ours = false;

        for (var i = 0; i < answers && offset < packet.Length; i++)
        {
            var name = ReadName(packet, ref offset);

            if (offset + 10 > packet.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;

            if (offset + length > packet.Length)
            {
                break;
            }

            var data = packet.Slice(offset, length);
            offset += length;

            if (name.Contains(ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                ours = true;
            }

            switch (type)
            {
                case TypeSrv when length >= 8:
                    port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                    break;

                case TypeTxt:
                    ReadText(data, ref product, ref machine, ref thumb);
                    break;
            }
        }

        if (!ours || port <= 0)
        {
            return false;
        }

        peer = new AnnouncedPeer
        {
            Address = from.Address.ToString(),
            Port = port,
            MachineName = machine ?? from.Address.ToString(),
            Product = product,
            ThumbprintPrefix = thumb,
        };

        return true;
    }

    private static void ReadText(ReadOnlySpan<byte> data, ref string? product, ref string? machine, ref string? thumb)
    {
        var cursor = 0;

        while (cursor < data.Length)
        {
            var length = data[cursor++];

            if (cursor + length > data.Length)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(data.Slice(cursor, length));
            cursor += length;

            var split = text.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                continue;
            }

            var key = text[..split];
            var value = text[(split + 1)..];

            switch (key)
            {
                case "product": product = value; break;
                case "machine": machine = value; break;
                case "thumb": thumb = value; break;
            }
        }
    }

    /// <summary>Читает имя, разворачивая сжатие по указателям (RFC 1035 §4.1.4).</summary>
    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var builder = new StringBuilder();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;

        while (cursor < packet.Length)
        {
            var length = packet[cursor];

            if (length == 0)
            {
                cursor++;
                break;
            }

            // Указатель. Число переходов ограничено: повреждённый или злонамеренный
            // пакет может ссылаться сам на себя, и наивный разбор зациклится.
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++jumps > 16)
                {
                    break;
                }

                var pointer = ((length & 0x3F) << 8) | packet[cursor + 1];

                if (!jumped)
                {
                    offset = cursor + 2;
                    jumped = true;
                }

                cursor = pointer;
                continue;
            }

            cursor++;

            if (cursor + length > packet.Length)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(packet.Slice(cursor, length)));
            cursor += length;
        }

        if (!jumped)
        {
            offset = cursor;
        }

        return builder.ToString();
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        }

        return builder.Length == 0 ? "storm-agent" : builder.ToString();
    }

    private static IPAddress LocalAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(MulticastGroup, MulticastPort);

            return (probe.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Any;
        }
        catch (SocketException)
        {
            return IPAddress.Any;
        }
    }
}
