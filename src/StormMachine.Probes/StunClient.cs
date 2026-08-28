using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace StormMachine.Probes;

/// <summary>Опрос серверов STUN с одного локального порта.</summary>
/// <remarks>
/// Локальная пара «адрес:порт» возвращается вместе с ответами не для полноты: без неё
/// нельзя отличить «трансляции нет» от «трансляция есть, но адрес совпал». Сравнивать
/// ответ сервера с портом другого сокета бессмысленно — порт был бы другим в любом случае.
/// </remarks>
public sealed record StunProbeResult
{
    public IPEndPoint? Local { get; init; }

    public required IReadOnlyList<StunReply> Replies { get; init; }
}

/// <summary>Что ответил один сервер STUN.</summary>
public sealed record StunReply
{
    public required string Server { get; init; }

    /// <summary>Адрес и порт, какими их увидел сервер. <c>null</c> — сервер не ответил.</summary>
    public IPEndPoint? Mapped { get; init; }

    /// <summary>Почему не получилось, если не получилось.</summary>
    public string? Failure { get; init; }

    public bool Answered => Mapped is not null;
}

/// <summary>
/// Клиент STUN (RFC 5389): узнать, каким адресом и портом машина видна снаружи.
/// </summary>
/// <remarks>
/// Внешний адрес берётся отсюда, а не у веб-службы «какой у меня IP», намеренно. Веб-служба
/// сообщает адрес, с которого пришёл HTTP-запрос — а он мог пройти через прокси, и тогда
/// это адрес прокси, а не машины. STUN отвечает на уровне UDP тем же путём, которым пойдёт
/// голос или видео, и поэтому говорит про ту трансляцию, которая реально мешает связи.
/// <para>
/// Второе следствие: один и тот же локальный порт, опрошенный у двух разных серверов,
/// показывает поведение NAT при отображении (RFC 4787 §4.1). Совпали пары «адрес:порт» —
/// отображение не зависит от адресата, и прямое соединение между узлами обычно
/// устанавливается. Разошлись — NAT выдаёт новый порт каждому адресату, и прямое
/// соединение потребует ретрансляции.
/// </para>
/// <para>
/// Поведение при фильтрации (кого NAT пускает обратно) здесь не определяется: для этого
/// нужен запрос CHANGE-REQUEST из RFC 5780, который поддерживают не все публичные серверы.
/// Молчать об этом нельзя, поэтому неопределённое так и называется неопределённым.
/// </para>
/// </remarks>
public static class StunClient
{
    /// <summary>Серверы по умолчанию. Опрашиваются только по явной команде оператора.</summary>
    public static IReadOnlyList<string> DefaultServers { get; } =
        ["stun.l.google.com:19302", "stun.cloudflare.com:3478"];

    private const int HeaderLength = 20;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const uint MagicCookie = 0x2112_A442;
    private const ushort AttributeMappedAddress = 0x0001;
    private const ushort AttributeXorMappedAddress = 0x0020;

    /// <summary>
    /// Опрашивает серверы с одного локального порта.
    /// </summary>
    /// <remarks>
    /// Именно с одного: сравнение отображений имеет смысл только при неизменной
    /// локальной паре «адрес:порт». Каждый запрос с нового порта дал бы разные ответы
    /// на любом NAT, и вывод «отображение зависит от адресата» был бы получен из
    /// устройства измерителя, а не из устройства сети.
    /// </remarks>
    public static async Task<StunProbeResult> QueryAsync(
        IReadOnlyList<string> servers,
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        if (servers.Count == 0)
        {
            return new StunProbeResult { Replies = [] };
        }

        var (firstHost, firstPort) = SplitServer(servers[0]);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Привязка к конкретному адресу, а не к 0.0.0.0: иначе сокет до отправки не знает
        // своего адреса и сравнивать с ответом сервера будет нечего.
        socket.Bind(new IPEndPoint(OutgoingAddressFor(firstHost, firstPort) ?? IPAddress.Any, 0));

        var replies = new List<StunReply>(servers.Count);

        foreach (var server in servers)
        {
            replies.Add(await QueryOneAsync(socket, server, timeoutMs, cancellationToken).ConfigureAwait(false));
        }

        return new StunProbeResult
        {
            Local = socket.LocalEndPoint as IPEndPoint,
            Replies = replies,
        };
    }

    /// <summary>Адрес, с которого система пошлёт пакеты к указанной цели.</summary>
    /// <remarks>
    /// Через UDP-«соединение», которое ничего не отправляет: система выбирает исходящий
    /// интерфейс по таблице маршрутизации, и это тот же выбор, который она сделает для
    /// настоящего трафика. Перебор адаптеров дал бы догадку вместо ответа системы.
    /// </remarks>
    private static IPAddress? OutgoingAddressFor(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, port);

            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<StunReply> QueryOneAsync(
        Socket socket,
        string server,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var (host, port) = SplitServer(server);

        IPEndPoint endpoint;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)
                .ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                return new StunReply { Server = server, Failure = "имя сервера не разрешилось" };
            }

            endpoint = new IPEndPoint(addresses[0], port);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new StunReply { Server = server, Failure = $"имя сервера не разрешилось: {ex.Message}" };
        }

        var transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);

        var request = BuildRequest(transactionId);
        var buffer = new byte[512];

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);

            await socket.SendToAsync(request, SocketFlags.None, endpoint, timeout.Token).ConfigureAwait(false);

            // Чужой ответ на этом же порту не считается своим: идентификатор транзакции
            // проверяется, и посторонний пакет просто не завершает ожидание.
            while (true)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                    .ConfigureAwait(false);

                var mapped = ParseResponse(buffer.AsSpan(0, received.ReceivedBytes), transactionId);

                if (mapped is not null)
                {
                    return new StunReply { Server = server, Mapped = mapped };
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new StunReply { Server = server, Failure = "не ответил за отведённое время" };
        }
        catch (SocketException ex)
        {
            return new StunReply { Server = server, Failure = ex.SocketErrorCode.ToString() };
        }
    }

    internal static byte[] BuildRequest(byte[] transactionId)
    {
        var packet = new byte[HeaderLength];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transactionId.CopyTo(packet.AsSpan(8));

        return packet;
    }

    /// <summary>
    /// Разбор ответа сервера. Открыт тестам: разбор двоичного формата с XOR-маской
    /// и выравниванием атрибутов — ровно тот код, ошибку в котором глазами не видно.
    /// </summary>
    internal static IPEndPoint? ParseResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> transactionId)
    {
        if (packet.Length < HeaderLength
            || BinaryPrimitives.ReadUInt16BigEndian(packet) != BindingSuccess
            || BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != MagicCookie
            || !packet.Slice(8, 12).SequenceEqual(transactionId))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var end = Math.Min(HeaderLength + length, packet.Length);
        var offset = HeaderLength;

        IPEndPoint? legacy = null;

        while (offset + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;

            if (offset + size > end)
            {
                break;
            }

            var value = packet.Slice(offset, size);

            if (type == AttributeXorMappedAddress && ReadAddress(value, packet, xor: true) is { } xored)
            {
                return xored;
            }

            if (type == AttributeMappedAddress)
            {
                legacy ??= ReadAddress(value, packet, xor: false);
            }

            // Значение атрибута дополняется до кратности четырём (RFC 5389 §15).
            offset += (size + 3) & ~3;
        }

        return legacy;
    }

    private static IPEndPoint? ReadAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> packet, bool xor)
    {
        if (value.Length < 8)
        {
            return null;
        }

        var family = value[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);

        if (xor)
        {
            port ^= (ushort)(MagicCookie >> 16);
        }

        if (family == 0x01)
        {
            Span<byte> address = stackalloc byte[4];
            value.Slice(4, 4).CopyTo(address);

            if (xor)
            {
                for (var i = 0; i < 4; i++)
                {
                    address[i] ^= packet[4 + i];
                }
            }

            return new IPEndPoint(new IPAddress(address), port);
        }

        if (family != 0x02 || value.Length < 20)
        {
            return null;
        }

        Span<byte> address6 = stackalloc byte[16];
        value.Slice(4, 16).CopyTo(address6);

        if (xor)
        {
            // Для IPv6 маска — «волшебное число» плюс идентификатор транзакции целиком.
            for (var i = 0; i < 16; i++)
            {
                address6[i] ^= packet[4 + i];
            }
        }

        return new IPEndPoint(new IPAddress(address6), port);
    }

    private static (string Host, int Port) SplitServer(string server)
    {
        var at = server.LastIndexOf(':');

        return at > 0 && int.TryParse(server[(at + 1)..], out var port)
            ? (server[..at], port)
            : (server, 3478);
    }
}
