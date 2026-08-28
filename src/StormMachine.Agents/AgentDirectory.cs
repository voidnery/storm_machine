using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Agents;
using StormMachine.Protocol;

namespace StormMachine.Agents;

/// <summary>
/// Сопряжение и связь с агентами со стороны клиента.
/// </summary>
/// <remarks>
/// Личность клиента создаётся один раз и живёт в базе рядом с сопряжениями. Новая
/// личность означала бы, что для всех агентов клиент стал незнакомцем — и каждое
/// сопряжение пришлось бы делать заново, а часть из них требует поездки на площадку.
/// </remarks>
public sealed class AgentDirectory(IAgentStore store) : IAgentDirectory, IDisposable
{
    private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    private PeerIdentity? _identity;

    public int DefaultPort => SecureChannel.DefaultPort;

    public async Task<string> GetOwnThumbprintAsync(CancellationToken cancellationToken = default) =>
        (await IdentityAsync(cancellationToken).ConfigureAwait(false)).Thumbprint;

    public async Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        return await _store.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ForgetAsync(string thumbprintOrName, CancellationToken cancellationToken = default)
    {
        var agent = await FindAsync(thumbprintOrName, cancellationToken).ConfigureAwait(false);

        return agent is not null
               && await _store.ForgetAsync(agent.Thumbprint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteAgent> RenameAsync(
        string thumbprintOrName,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var agent = await FindAsync(thumbprintOrName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Агент «{thumbprintOrName}» не найден.");

        var renamed = agent with { Alias = name.Trim() };
        await _store.SaveAsync(renamed, cancellationToken).ConfigureAwait(false);

        return renamed;
    }

    public async Task<RemoteAgent> PairByDialingAsync(
        string host,
        int port,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // Приводим к сравнимому виду здесь: как оператор набрал код — с дефисом,
        // в нижнем регистре, с пробелами — забота ввода, а не сопряжения.
        var options = await OptionsAsync(PairingCode.Normalize(code), cancellationToken).ConfigureAwait(false);

        return await TranslateAsync(async () =>
        {
            using var session = await SecureChannel
                .ConnectAsync(host, port, options, cancellationToken)
                .ConfigureAwait(false);

            // Адрес запоминается тот, по которому дозвонились, а не тот, что назвал агент:
            // поток данных пойдёт по нему же, и чужому слову тут верить нельзя.
            return await RememberAsync(session, AgentDirection.ClientDials, host, port, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<RemoteAgent> PairByWaitingAsync(
        int port,
        IProgress<PairingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var offer = PairingOffer.Issue();
        var options = await OptionsAsync(offer.Code, cancellationToken).ConfigureAwait(false);
        var listener = new TcpListener(IPAddress.Any, port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Порт {port} занять не удалось: {ex.SocketErrorCode}. "
                + "Либо на нём уже ждёт другое сопряжение, либо порт занят чужой программой.",
                ex);
        }

        progress?.Report(new PairingProgress(
            $"Жду звонка агента на порт {port}. На его машине выполни: "
            + $"storm-agent connect <адрес этой машины> --код {offer.ForHumans}"
            + $"{Environment.NewLine}Код одноразовый и годен {offer.Lifetime.TotalMinutes:0} мин.",
            offer.ForHumans,
            IsDone: false));

        // Ожидание кончается вместе со сроком кода: висеть с годным кодом дольше,
        // чем сам код годен, — обещание, которое некому выполнить.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(offer.Lifetime);

        try
        {
            // Входящие на этой машине тоже заблокированы по умолчанию — но здесь
            // это машина оператора, где права есть и разовое разрешение уместно.
            // Именно поэтому направление вообще стало выбором, а не устройством продукта.
            var agent = await TranslateAsync(async () =>
            {
                var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);

                using var session = await SecureChannel
                    .AcceptAsync(client, options, deadline.Token)
                    .ConfigureAwait(false);

                return await RememberAsync(session, AgentDirection.AgentDials, null, port, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

            offer.Consume();
            progress?.Report(new PairingProgress($"Сопряжён {agent.DisplayName}.", null, IsDone: true));

            return agent;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Агент не позвонил за {offer.Lifetime.TotalMinutes:0} мин, и срок кода истёк. "
                + "Запусти сопряжение заново — код будет новый.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<RemoteAgent> CheckAsync(string thumbprintOrName, CancellationToken cancellationToken = default)
    {
        var agent = await FindAsync(thumbprintOrName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Агент «{thumbprintOrName}» не найден.");

        if (agent.Direction == AgentDirection.AgentDials)
        {
            throw new InvalidOperationException(
                $"К агенту «{agent.DisplayName}» звоним не мы, а он. Проверить связь по своей "
                + "воле нельзя — дождись, пока он подключится.");
        }

        if (agent.Address is not { Length: > 0 } address)
        {
            throw new InvalidOperationException($"У агента «{agent.DisplayName}» не записан адрес.");
        }

        var options = await OptionsAsync(null, cancellationToken).ConfigureAwait(false);

        return await TranslateAsync(async () =>
        {
            using var session = await SecureChannel
                .ConnectAsync(address, agent.Port, options, cancellationToken)
                .ConfigureAwait(false);

            await session.Channel
                .SendAsync(new ProtocolMessage { Kind = MessageKind.Ping, Exchange = 1 }, cancellationToken)
                .ConfigureAwait(false);

            var answer = await session.Channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (answer?.Kind != MessageKind.Pong)
            {
                throw new ProtocolException(
                    $"На проверку связи агент ответил {answer?.Kind.ToString() ?? "молчанием"}.");
            }

            return await RememberAsync(session, agent.Direction, address, agent.Port, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscoveredAgent>> BrowseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var announced = await TranslateAsync(() => LocalDiscovery.BrowseAsync(duration, cancellationToken))
            .ConfigureAwait(false);
        var known = await ListAsync(cancellationToken).ConfigureAwait(false);

        // Уже сопряжённые не прячутся, а помечаются: увидеть в эфире знакомого агента
        // — это ответ на вопрос «жив ли он», и убирать его из списка значило бы
        // выбросить полезное сведение ради опрятности.
        return
        [
            .. announced.Select(a => new DiscoveredAgent(
                a.Address,
                a.Port,
                a.MachineName,
                a.Product,
                a.ThumbprintPrefix,
                known.Any(k => a.ThumbprintPrefix is { Length: > 0 } prefix
                               && k.Thumbprint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))),
        ];
    }

    /// <summary>Сколько ждать звонка агента, прежде чем признать, что он не придёт.</summary>
    public static TimeSpan CallTimeout => TimeSpan.FromMinutes(2);

    /// <summary>
    /// Открывает соединение с сопряжённым агентом.
    /// </summary>
    /// <remarks>
    /// Направление берётся из записи агента, а не выбирается заново: отсутствие прав
    /// на площадке верно и завтра. Если звонит агент, здесь ждут его звонка, а не
    /// отказывают: оператор попросил измерить, и «нельзя» вместо «жду» заставило бы
    /// его искать вторую команду там, где нужна одна.
    /// </remarks>
    public async Task<SecureSession> OpenAsync(
        RemoteAgent agent,
        IProgress<PairingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var options = await OptionsAsync(null, cancellationToken).ConfigureAwait(false);

        if (agent.Direction == AgentDirection.ClientDials)
        {
            if (agent.Address is not { Length: > 0 } address)
            {
                throw new InvalidOperationException($"У агента «{agent.DisplayName}» не записан адрес.");
            }

            return await SecureChannel
                .ConnectAsync(address, agent.Port, options, cancellationToken)
                .ConfigureAwait(false);
        }

        return await WaitForCallAsync(agent, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ждёт звонка от конкретного агента.
    /// </summary>
    /// <remarks>
    /// Чужой звонок на этот порт не прерывает ожидание: соединение закрывается,
    /// и ожидание продолжается. Взять первого дозвонившегося значило бы измерить
    /// не тот канал и не сказать об этом.
    /// </remarks>
    private async Task<SecureSession> WaitForCallAsync(
        RemoteAgent agent,
        ChannelOptions options,
        IProgress<PairingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var port = agent.Port > 0 ? agent.Port : SecureChannel.DefaultPort;
        var listener = new TcpListener(IPAddress.Any, port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Порт {port} занять не удалось: {ex.SocketErrorCode}. "
                + "Либо на нём уже ждут, либо порт занят чужой программой.",
                ex);
        }

        progress?.Report(new PairingProgress(
            $"Жду звонка агента «{agent.DisplayName}» на порт {port}. "
            + $"На его машине: storm-agent connect <адрес этой машины>",
            null,
            IsDone: false));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CallTimeout);

        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                var session = await SecureChannel.AcceptAsync(client, options, deadline.Token).ConfigureAwait(false);

                if (string.Equals(session.Peer.Thumbprint, agent.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    await RememberAsync(session, agent.Direction, session.Peer.Address, port, cancellationToken)
                        .ConfigureAwait(false);

                    return session;
                }

                session.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Агент «{agent.DisplayName}» не позвонил за {CallTimeout.TotalMinutes:0} мин. "
                + "Запусти на его машине: storm-agent connect <адрес этой машины>.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        return await _store.FindAsync(thumbprintOrName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteAgent> RememberAsync(
        SecureSession session,
        AgentDirection direction,
        string? address,
        int port,
        CancellationToken cancellationToken)
    {
        var existing = await _store.FindAsync(session.Peer.Thumbprint, cancellationToken).ConfigureAwait(false);

        var agent = new RemoteAgent
        {
            Thumbprint = session.Peer.Thumbprint,
            MachineName = session.Peer.MachineName,
            Product = session.Peer.Product,
            Address = address ?? session.Peer.Address,
            Port = port,
            Direction = direction,
            PairedUtc = existing?.PairedUtc ?? DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Capabilities = session.Peer.Capabilities,
            Alias = existing?.Alias,
        };

        await _store.SaveAsync(agent, cancellationToken).ConfigureAwait(false);

        return agent;
    }

    private async Task<ChannelOptions> OptionsAsync(string? code, CancellationToken cancellationToken)
    {
        var identity = await IdentityAsync(cancellationToken).ConfigureAwait(false);
        var agents = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return new ChannelOptions
        {
            Identity = identity,
            KnownThumbprints = [.. agents.Select(a => a.Thumbprint)],
            PairingCode = code,
            ProductName = $"storm/{ProductInfo.Version}",
            Capabilities = [Protocol.Capabilities.TcpThroughput, Protocol.Capabilities.UdpQuality],
        };
    }

    /// <summary>
    /// Личность клиента: одна на установку, создаётся при первом обращении.
    /// </summary>
    /// <remarks>
    /// Хранится в базе, а не в файле рядом: резервная копия одного файла обязана
    /// возвращать работающую установку целиком, включая способность подключиться
    /// к уже сопряжённым агентам.
    /// </remarks>
    public void Dispose() => _identityGate.Dispose();

    /// <summary>
    /// Переводит отказ провода в отказ, о котором знает слой приложения.
    /// </summary>
    /// <remarks>
    /// Сообщение сохраняется дословно: его писали для оператора, и переписывать его
    /// здесь значило бы завести второй, расходящийся с первым, текст об одном и том же.
    /// Меняется только тип — чтобы показу было что ловить, не ссылаясь на протокол.
    /// </remarks>
    private static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            throw new AgentException(ex.Message, ex);
        }
    }

    private async Task<PeerIdentity> IdentityAsync(CancellationToken cancellationToken)
    {
        if (_identity is not null)
        {
            return _identity;
        }

        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_identity is not null)
            {
                return _identity;
            }

            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var stored = await _store.LoadIdentityAsync(cancellationToken).ConfigureAwait(false);

            if (stored is { Length: > 0 })
            {
                _identity = PeerIdentity.FromContainer(stored);

                return _identity;
            }

            var (identity, container) = PeerIdentity.CreateWithContainer("storm-client");
            await _store.SaveIdentityAsync(container, cancellationToken).ConfigureAwait(false);

            _identity = identity;

            return _identity;
        }
        finally
        {
            _identityGate.Release();
        }
    }
}
