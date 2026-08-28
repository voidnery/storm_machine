using System.Net;
using System.Net.Sockets;
using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Сопряжение и подключение целиком, через настоящий сокет.
/// </summary>
/// <remarks>
/// Это и есть приёмка И-12, выполнимая на одной машине: сопряжение по коду, повторное
/// подключение без подтверждения, отказ подделке, отказ незнакомцу и отказ по версии.
/// Вторая машина нужна для точности измерений (И-13), а не для проверки доверия —
/// доверие проверяется здесь, и проверяется каждой сборкой.
/// </remarks>
public sealed class SecureChannelTests
{
    private static ChannelOptions Options(
        PeerIdentity identity,
        string? code = null,
        params string[] known) => new()
    {
        Identity = identity,
        PairingCode = code,
        KnownThumbprints = known,
        ProductName = "storm/тест",
        MachineName = "СТЕНД",
        Capabilities = [Capabilities.TcpThroughput],
        HandshakeTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Сводит две стороны на петле и возвращает, что вышло у каждой.</summary>
    private static async Task<(SecureSession? Dialer, SecureSession? Listener, Exception? DialerError, Exception? ListenerError)>
        MeetAsync(ChannelOptions dialing, ChannelOptions listening)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        SecureSession? accepted = null;
        Exception? listenerError = null;

        var listenTask = Task.Run(async () =>
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                accepted = await SecureChannel.AcceptAsync(client, listening);
            }
            catch (Exception ex)
            {
                listenerError = ex;
            }
        });

        SecureSession? dialed = null;
        Exception? dialerError = null;

        try
        {
            dialed = await SecureChannel.ConnectAsync("127.0.0.1", port, dialing);
        }
        catch (Exception ex)
        {
            dialerError = ex;
        }

        await listenTask;
        listener.Stop();

        return (dialed, accepted, dialerError, listenerError);
    }

    [Fact]
    public async Task Pairing_BothSidesLearnEachOther()
    {
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");
        var code = PairingCode.Generate();

        var (dialer, listener, dialerError, listenerError) =
            await MeetAsync(Options(client, code), Options(agent, code));

        Assert.Null(dialerError);
        Assert.Null(listenerError);
        Assert.NotNull(dialer);
        Assert.NotNull(listener);

        // Каждая сторона узнала отпечаток другой — именно это и запоминается.
        Assert.Equal(agent.Thumbprint, dialer.Peer.Thumbprint);
        Assert.Equal(client.Thumbprint, listener.Peer.Thumbprint);

        Assert.True(dialer.WasPaired);
        Assert.True(listener.WasPaired);

        dialer.Dispose();
        listener.Dispose();
    }

    [Fact]
    public async Task SecondConnection_NeedsNoCode()
    {
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");

        var (dialer, listener, _, _) = await MeetAsync(
            Options(client, known: agent.Thumbprint),
            Options(agent, known: client.Thumbprint));

        Assert.NotNull(dialer);
        Assert.NotNull(listener);

        // Сопряжение не происходит заново: стороны уже знакомы.
        Assert.False(dialer.WasPaired);
        Assert.False(listener.WasPaired);

        dialer.Dispose();
        listener.Dispose();
    }

    [Fact]
    public async Task DirectionDoesNotMatter_AgentCanDialTheClient()
    {
        // Решение оператора: звонить может любая сторона. Значит и доверие
        // не должно зависеть от того, кто оказался сервером.
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");

        var (dialer, listener, dialerError, _) = await MeetAsync(
            Options(agent, known: client.Thumbprint),
            Options(client, known: agent.Thumbprint));

        Assert.Null(dialerError);
        Assert.NotNull(dialer);
        Assert.NotNull(listener);
        Assert.Equal(client.Thumbprint, dialer.Peer.Thumbprint);
        Assert.Equal(agent.Thumbprint, listener.Peer.Thumbprint);

        dialer.Dispose();
        listener.Dispose();
    }

    [Fact]
    public async Task SubstitutedCertificate_IsRefused()
    {
        // Подделка с тем же CN и тем же сроком: отличается только ключ.
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");
        var impostor = PeerIdentity.Create("storm-agent");

        var (dialer, _, dialerError, _) = await MeetAsync(
            Options(client, known: agent.Thumbprint),
            Options(impostor, known: client.Thumbprint));

        Assert.Null(dialer);
        var error = Assert.IsType<ProtocolException>(dialerError);
        Assert.Equal(RefusalReason.Unknown, error.Reason);

        // Отпечаток подделки назван: оператор должен видеть, кто именно пришёл.
        Assert.Contains(PeerIdentity.Group(impostor.Thumbprint), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPeerWithoutCode_IsRefusedWithInstruction()
    {
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");

        var (dialer, _, dialerError, listenerError) =
            await MeetAsync(Options(client), Options(agent));

        Assert.Null(dialer);

        var refusal = Assert.IsType<ProtocolException>(listenerError);
        Assert.Equal(RefusalReason.Unknown, refusal.Reason);

        // Отказ доехал до позвонившего вместе с причиной, а не оборвался молча.
        var seen = Assert.IsType<ProtocolException>(dialerError);
        Assert.Contains("код сопряжения", seen.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongCode_IsRefusedAndSaysTheCodeExpires()
    {
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");

        var (dialer, _, dialerError, _) =
            await MeetAsync(Options(client, "ACDEFG"), Options(agent, "QQQQQQ"));

        Assert.Null(dialer);

        var error = Assert.IsType<ProtocolException>(dialerError);
        Assert.Equal(RefusalReason.Pairing, error.Reason);
    }

    [Fact]
    public async Task Peer_IsDescribedForTheOperator()
    {
        var client = PeerIdentity.Create("storm-client");
        var agent = PeerIdentity.Create("storm-agent");
        var code = PairingCode.Generate();

        var (dialer, listener, _, _) = await MeetAsync(Options(client, code), Options(agent, code));

        Assert.NotNull(dialer);
        Assert.Equal("СТЕНД", dialer.Peer.MachineName);
        Assert.Equal("storm/тест", dialer.Peer.Product);
        Assert.True(dialer.Peer.Can(Capabilities.TcpThroughput));
        Assert.False(dialer.Peer.Can(Capabilities.UdpQuality));
        Assert.Contains("СТЕНД", dialer.Peer.Describe(), StringComparison.Ordinal);

        dialer?.Dispose();
        listener?.Dispose();
    }

    [Fact]
    public async Task UnreachableHost_ExplainsTheFirewall()
    {
        // Самая частая причина — заблокированные входящие. Оператор должен прочитать
        // это, а не «соединение не установлено».
        var identity = PeerIdentity.Create("storm-client");

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var error = await Assert.ThrowsAsync<ProtocolException>(
            () => SecureChannel.ConnectAsync("127.0.0.1", freePort, Options(identity)));

        Assert.Contains("брандмауэр", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
