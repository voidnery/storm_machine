using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StormMachine.Protocol;

/// <summary>Кто оказался на другом конце.</summary>
public sealed record PeerInfo
{
    public required string Thumbprint { get; init; }

    public required string Product { get; init; }

    public required string MachineName { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Адрес, с которого пришло соединение.
    /// </summary>
    /// <remarks>
    /// Берётся из сокета, а не из того, что собеседник о себе сказал. Поток данных
    /// пойдёт по этому адресу, и верить здесь чужому слову нельзя: измерение ушло бы
    /// не туда, а на той стороне никто бы этого не заметил.
    /// </remarks>
    public string? Address { get; init; }

    public bool Can(string capability) =>
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);

    public string Describe() => $"{MachineName} ({Product})";
}

/// <summary>С чем сторона выходит на связь.</summary>
public sealed record ChannelOptions
{
    public required PeerIdentity Identity { get; init; }

    /// <summary>Отпечатки уже сопряжённых собеседников. Пусто — сопряжений нет.</summary>
    public IReadOnlyCollection<string> KnownThumbprints { get; init; } = [];

    /// <summary>Код сопряжения. Задан — принимаем незнакомого, доказавшего знание кода.</summary>
    public string? PairingCode { get; init; }

    public string ProductName { get; init; } = "storm";

    public string MachineName { get; init; } = Environment.MachineName;

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Установленное соединение с опознанным собеседником.</summary>
public sealed class SecureSession(MessageChannel channel, PeerInfo peer, bool wasPaired) : IDisposable
{
    public MessageChannel Channel { get; } = channel;

    public PeerInfo Peer { get; } = peer;

    /// <summary>Сопряжение произошло именно сейчас — отпечаток надо запомнить.</summary>
    public bool WasPaired { get; } = wasPaired;

    public void Dispose() => Channel.Dispose();
}

/// <summary>
/// Установление соединения: взаимный TLS и взаимный пиннинг.
/// </summary>
/// <remarks>
/// Пиннинг взаимный, а не односторонний, потому что звонить может любая сторона —
/// это решение оператора, зафиксированное перед И-12. Будь доверие построено на том,
/// кто оказался сервером, смена направления меняла бы и то, кто кого проверяет,
/// а проверять надо всегда обоим.
/// <para>
/// Отсюда же следует симметрия кода: <see cref="ConnectAsync"/> и <see cref="AcceptAsync"/>
/// отличаются только тем, кто говорит первым. Всё остальное — проверка версии, сверка
/// отпечатка, разбор кода сопряжения — общее, и написано один раз.
/// </para>
/// </remarks>
public static class SecureChannel
{
    /// <summary>Порт управляющего канала по умолчанию.</summary>
    public const int DefaultPort = 47820;

    /// <summary>Имя, под которым сторона представляется в TLS. Проверяется отпечаток, не имя.</summary>
    private const string TargetName = "storm-peer";

    public static async Task<SecureSession> ConnectAsync(
        string host,
        int port,
        ChannelOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);

        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            client.Dispose();

            throw new ProtocolException(
                $"До {host}:{port} не достучаться: {ex.SocketErrorCode}. "
                + "Проверь, что собеседник запущен и что входящие на этот порт разрешены "
                + "его брандмауэром — по умолчанию Windows их блокирует.",
                ex);
        }

        return await EstablishAsync(client, options, dialing: true, cancellationToken).ConfigureAwait(false);
    }

    public static Task<SecureSession> AcceptAsync(
        TcpClient client,
        ChannelOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        return EstablishAsync(client, options, dialing: false, cancellationToken);
    }

    private static async Task<SecureSession> EstablishAsync(
        TcpClient client,
        ChannelOptions options,
        bool dialing,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);

        X509Certificate2? peerCertificate = null;

        var ssl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                // Цепочка не проверяется намеренно: у портативного агента нет центра
                // сертификации, и проверка по цепочке отвергла бы настоящий сертификат
                // тоже (спайк-05). Решение принимается по отпечатку, ниже по коду,
                // когда уже известно, сопряжение это или обычное подключение.
                peerCertificate = certificate is null
                    ? null
                    : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                return peerCertificate is not null;
            });

        try
        {
            if (dialing)
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = TargetName,
                        ClientCertificates = [options.Identity.Certificate],
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                    },
                    timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = options.Identity.Certificate,

                        // Взаимный TLS: сертификат требуется и от того, кто позвонил.
                        // Без этого сторона, принимающая соединение, не знала бы,
                        // кто к ней пришёл, до первого же сообщения — а верить
                        // сообщению, не проверив предъявителя, нельзя.
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                    },
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch (AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();

            throw new ProtocolException(
                "Рукопожатие TLS не состоялось: " + (ex.InnerException?.Message ?? ex.Message),
                ex);
        }
        catch (Exception)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }

        if (peerCertificate is null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();

            throw new ProtocolException("Собеседник не предъявил сертификат.");
        }

        var peerThumbprint = PeerIdentity.ThumbprintOf(peerCertificate.RawData);
        peerCertificate.Dispose();

        // Адрес приводится к IPv4, если это отображённый IPv4. Сокет .NET по умолчанию
        // двухстековый, и адрес 127.0.0.1 приходит из него как ::ffff:127.0.0.1 — тот же
        // адрес в другой записи. Записать его так значило бы отдать потоку данных имя,
        // которое разбор адресов IPv4 не принимает.
        var endpoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;

        var address = endpoint?.Address is { } peerAddress
            ? (peerAddress.IsIPv4MappedToIPv6 ? peerAddress.MapToIPv4() : peerAddress).ToString()
            : null;
        var channel = new MessageChannel(ssl);

        try
        {
            var (peer, paired) = dialing
                ? await GreetAsync(channel, options, peerThumbprint, timeout.Token).ConfigureAwait(false)
                : await AnswerAsync(channel, options, peerThumbprint, timeout.Token).ConfigureAwait(false);

            return new SecureSession(channel, peer with { Address = address }, paired);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>Сторона, позвонившая первой: говорит Hello и ждёт Welcome.</summary>
    private static async Task<(PeerInfo Peer, bool Paired)> GreetAsync(
        MessageChannel channel,
        ChannelOptions options,
        string peerThumbprint,
        CancellationToken cancellationToken)
    {
        await channel.SendAsync(Hello(options, peerThumbprint), cancellationToken).ConfigureAwait(false);

        var answer = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new ProtocolException("Собеседник закрыл соединение, не ответив на представление.");

        if (answer.Kind == MessageKind.Refused)
        {
            throw new ProtocolException(
                answer.Explanation ?? "Собеседник отказал без объяснения.",
                answer.Reason ?? RefusalReason.Unsupported);
        }

        if (answer.Kind != MessageKind.Welcome)
        {
            throw new ProtocolException($"Ожидалось Welcome, пришло {answer.Kind}.");
        }

        return Judge(answer, options, peerThumbprint);
    }

    /// <summary>Сторона, принявшая звонок: слушает Hello, судит и отвечает.</summary>
    private static async Task<(PeerInfo Peer, bool Paired)> AnswerAsync(
        MessageChannel channel,
        ChannelOptions options,
        string peerThumbprint,
        CancellationToken cancellationToken)
    {
        var hello = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ProtocolException("Собеседник закрыл соединение, не представившись.");

        if (hello.Kind != MessageKind.Hello)
        {
            await RefuseAsync(channel, RefusalReason.Unsupported,
                $"Первым сообщением ожидается Hello, пришло {hello.Kind}.", cancellationToken)
                .ConfigureAwait(false);

            throw new ProtocolException($"Первым сообщением ожидается Hello, пришло {hello.Kind}.");
        }

        PeerInfo peer;
        bool paired;

        try
        {
            (peer, paired) = Judge(hello, options, peerThumbprint);
        }
        catch (ProtocolException ex)
        {
            // Отказ объясняется собеседнику, а не только своему оператору: на той стороне
            // тоже человек, и «соединение закрыто» не скажет ему, что делать.
            await RefuseAsync(channel, ex.Reason ?? RefusalReason.Unsupported, ex.Message, cancellationToken)
                .ConfigureAwait(false);

            throw;
        }

        await channel.SendAsync(
            Hello(options, peerThumbprint) with { Kind = MessageKind.Welcome, Exchange = hello.Exchange },
            cancellationToken).ConfigureAwait(false);

        return (peer, paired);
    }

    /// <summary>
    /// Общее решение обеих сторон: версия, подлинность предъявителя, право на сопряжение.
    /// </summary>
    /// <remarks>
    /// Порядок проверок — от того, что нельзя починить настройкой, к тому, что можно.
    /// Несовпадение версий не лечится кодом сопряжения, и сообщать про код раньше
    /// значило бы отправить оператора не в ту сторону.
    /// </remarks>
    private static (PeerInfo Peer, bool Paired) Judge(
        ProtocolMessage message,
        ChannelOptions options,
        string peerThumbprint)
    {
        if (!ProtocolVersion.IsCompatibleWith(message.ProtocolMajor))
        {
            throw new ProtocolException(
                ProtocolVersion.Explain(message.ProtocolMajor, message.ProtocolMinor,
                    message.Product ?? "неизвестный продукт"),
                RefusalReason.Version);
        }

        // Заявленный отпечаток обязан совпасть с предъявленным в TLS. Расхождение
        // означает, что сообщение составлял не тот, кто держит ключ.
        if (message.Thumbprint is { Length: > 0 } declared
            && !string.Equals(declared, peerThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtocolException(
                "Собеседник назвал отпечаток, не совпадающий с предъявленным сертификатом.",
                RefusalReason.Thumbprint);
        }

        var known = options.KnownThumbprints.Contains(peerThumbprint, StringComparer.OrdinalIgnoreCase);

        var peer = new PeerInfo
        {
            Thumbprint = peerThumbprint,
            Product = message.Product ?? "неизвестный продукт",
            MachineName = message.MachineName ?? "неизвестная машина",
            Capabilities = message.Capabilities ?? [],
        };

        if (known)
        {
            return (peer, false);
        }

        if (options.PairingCode is not { Length: > 0 } code)
        {
            throw new ProtocolException(
                $"Собеседник {peer.Describe()} не сопряжён с нами. "
                + $"Его отпечаток: {PeerIdentity.Group(peerThumbprint)}. "
                + "Для первого соединения нужен код сопряжения.",
                RefusalReason.Unknown);
        }

        if (!PairingCode.Verify(message.PairingProof, code, options.Identity.Thumbprint, peerThumbprint))
        {
            throw new ProtocolException(
                "Код сопряжения не подошёл. Проверь, что набран тот код, который агент "
                + "показывает сейчас: код одноразовый и живёт ограниченное время.",
                RefusalReason.Pairing);
        }

        return (peer, true);
    }

    private static ProtocolMessage Hello(ChannelOptions options, string peerThumbprint) => new()
    {
        Kind = MessageKind.Hello,
        ProtocolMajor = ProtocolVersion.Major,
        ProtocolMinor = ProtocolVersion.Minor,
        Product = options.ProductName,
        MachineName = options.MachineName,
        Thumbprint = options.Identity.Thumbprint,
        Capabilities = options.Capabilities,
        PairingProof = options.PairingCode is { Length: > 0 } code
            ? PairingCode.Prove(code, options.Identity.Thumbprint, peerThumbprint)
            : null,
    };

    private static async Task RefuseAsync(
        MessageChannel channel,
        RefusalReason reason,
        string explanation,
        CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(
                new ProtocolMessage
                {
                    Kind = MessageKind.Refused,
                    Reason = reason,
                    Explanation = explanation,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
        {
            // Объяснить не удалось — соединение уже разорвано. Своя ошибка важнее.
        }
    }
}
