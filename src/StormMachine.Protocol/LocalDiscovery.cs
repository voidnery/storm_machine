using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StormMachine.Protocol;

/// <summary>Агент, объявивший о себе в локальной сети.</summary>
public sealed record AnnouncedAgent
{
    public required string Address { get; init; }

    public required int Port { get; init; }

    public required string MachineName { get; init; }

    public string? Product { get; init; }

    /// <summary>Начало отпечатка: сверить с полным при сопряжении.</summary>
    public string? ThumbprintPrefix { get; init; }

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

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var target = new IPEndPoint(MulticastGroup, MulticastPort);
        var packet = BuildAnnouncement(machineName, port, product, thumbprint, LocalAddress());

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await socket.SendToAsync(packet, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановлено — объявлять больше нечего.
        }
        catch (SocketException)
        {
            // Групповая рассылка недоступна (нет маршрута, запрещена политикой).
            // Это не повод останавливать агента: обнаружение — удобство, а не связь.
        }
    }

    /// <summary>Слушает объявления заданное время.</summary>
    public static async Task<IReadOnlyList<AnnouncedAgent>> BrowseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        var found = new Dictionary<string, AnnouncedAgent>(StringComparer.OrdinalIgnoreCase);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(MulticastGroup, IPAddress.Any));
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
                        (IPEndPoint)received.RemoteEndPoint, out var agent))
                {
                    found[agent.MachineName] = agent;
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

        return [.. found.Values.OrderBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Собирает объявление: PTR, SRV, TXT и A одним пакетом.
    /// </summary>
    /// <remarks>
    /// Без сжатия имён по указателям. Сжатие экономит десятки байт в пакете, который
    /// уходит раз в десять секунд, и стоит самого хитрого места в разборе DNS —
    /// цена и выгода тут несопоставимы.
    /// </remarks>
    private static byte[] BuildAnnouncement(
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
    private static bool TryReadAnnouncement(
        ReadOnlySpan<byte> packet,
        IPEndPoint? from,
        out AnnouncedAgent agent)
    {
        agent = default!;

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

        agent = new AnnouncedAgent
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
