using System.Net;
using System.Net.Sockets;
using StormMachine.Protocol;

namespace StormMachine.Agent;

/// <summary>Где агент хранит своё и с чем работает.</summary>
public sealed record AgentSettings
{
    public required string IdentityPath { get; init; }

    public required string PeerBookPath { get; init; }

    public int Port { get; init; } = SecureChannel.DefaultPort;

    /// <summary>
    /// Предложение сопряжения. Пусто — новых собеседников не принимаем.
    /// </summary>
    /// <remarks>
    /// Не строка, а предложение со сроком и признаком использования: код диктуют вслух,
    /// и после того, как им воспользовались, он обязан перестать работать.
    /// </remarks>
    public PairingOffer? Pairing { get; init; }

    /// <summary>
    /// За агентом сидит человек: запуск из консоли, а не службой.
    /// </summary>
    /// <remarks>
    /// Различие нужно ровно для одного: кому показывать код сопряжения. Человеку
    /// у консоли его показать можно и нужно, а служба работает без него — выдавать
    /// код в журнал событий, которого никто не читает, бессмысленно.
    /// </remarks>
    public bool Interactive { get; init; }

    /// <summary>Куда писать происходящее. По умолчанию в консоль.</summary>
    public Action<string> Log { get; init; } = Console.WriteLine;

    /// <summary>
    /// Кому рассказывать о происходящем числами, а не текстом.
    /// </summary>
    /// <remarks>
    /// Пусто у консоли — ей довольно строк журнала. Окно подписывается сюда
    /// и рисует график прямо по снимкам измерения.
    /// </remarks>
    public Action<AgentMoment>? Watch { get; init; }

    /// <summary>
    /// Настройки агента, живущего рядом со своим исполняемым файлом.
    /// </summary>
    /// <remarks>
    /// В этом и состоит портативность: папку с агентом можно скопировать на флешку
    /// вместе с личностью и сопряжениями, и на другой машине он останется тем же
    /// собеседником. Запись в профиль пользователя такую переносимость сломала бы.
    /// <para>
    /// Правило одно на всех: и консоль, и окно зовут этот метод. Два вычисления пути
    /// однажды разошлись бы, и окно стало бы другим агентом — с другим отпечатком
    /// и чужой книгой сопряжений.
    /// </para>
    /// </remarks>
    public static AgentSettings Beside(int port) => new()
    {
        IdentityPath = Path.Combine(Folder, "storm-agent.identity.pfx"),
        PeerBookPath = Path.Combine(Folder, "storm-agent.peers.json"),
        Port = port,
    };

    private static string Folder =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}

/// <summary>
/// Агент: принимает соединения или дозванивается сам и делает свою половину измерений.
/// </summary>
/// <remarks>
/// Обе роли живут здесь вместе, потому что различаются они ровно одним: кто установил
/// соединение. Дальше идёт один и тот же разговор, и разводить его по двум реализациям
/// значило бы завести два поведения там, где поведение одно.
/// <para>
/// Направление выбирает оператор при сопряжении — решение, принятое перед И-12 после
/// спайка-05. Входящие на Windows заблокированы по умолчанию во всех трёх профилях,
/// а правило требует прав администратора; режим дозвона существует ровно для того,
/// чтобы портативный агент работал там, где прав нет.
/// </para>
/// </remarks>
public sealed class AgentHost(AgentSettings settings)
{
    private readonly AgentSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private PeerIdentity? _identity;
    private PeerBook? _book;
    private int _busy;

    /// <summary>Код, выданный агентом самостоятельно, — когда сопрягаться больше не с кем.</summary>
    private PairingOffer? _offered;

    /// <summary>Код, названный клиентом, — с ним агент звонит сам.</summary>
    /// <remarks>
    /// Отдельно от <see cref="AgentSettings.Pairing" />: тот код агент выдаёт, этот —
    /// получает. Путать их нельзя, иначе звонок предъявил бы собеседнику свой код
    /// вместо чужого и получил бы отказ.
    /// </remarks>
    private PairingOffer? _dialing;

    public PeerIdentity Identity => _identity ??= PeerIdentity.LoadOrCreate(_settings.IdentityPath, "storm-agent");

    public PeerBook Book => _book ??= new PeerBook(_settings.PeerBookPath);

    /// <summary>Слушает и обслуживает каждого, кто дозвонился.</summary>
    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _settings.Port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new ProtocolException(
                $"Порт {_settings.Port} занять не удалось: {ex.SocketErrorCode}. "
                + "Либо на нём уже слушает другой агент, либо порт занят чужой программой.",
                ex);
        }

        _settings.Log($"Слушаю порт {_settings.Port}. Отпечаток: {Identity.ThumbprintForHumans}");

        Tell(new AgentMoment
        {
            Kind = AgentMomentKind.Listening,
            Text = $"Слушаю порт {_settings.Port}.",
        });

        // Код выдаётся сам, когда сопрягаться больше не с кем.
        //
        // Раньше агент, запущенный без ключей, сообщал, что никого принять не сможет,
        // и предлагал перезапустить себя с ключом «--сопряжение». Других полезных
        // действий в этом состоянии у него нет: слушающий агент без единого сопряжения
        // не умеет ровно ничего. Отправлять человека за флагом ради единственно
        // возможного шага — та же беда, что и команда консоли в окне: продукт знает,
        // что нужно, и всё равно просит сделать это руками (замечание оператора, И-25).
        //
        // Служба так не делает: кода там никто не увидит, а в журнале событий он
        // был бы только шумом.
        var pairing = _settings.Pairing;

        if (pairing is null && _settings.Interactive && Book.Thumbprints.Count == 0)
        {
            pairing = _offered = PairingOffer.Issue();

            _settings.Log("Сопряжений ещё нет — выдаю код сам:");
        }

        if (pairing is { } offer)
        {
            _settings.Log($"Код сопряжения: {offer.ForHumans}");
            _settings.Log($"Код одноразовый и годен {offer.Lifetime.TotalMinutes:0} мин — "
                          + "после сопряжения по нему второй раз сопрячься нельзя. "
                          + "Истёк — перезапусти агента, он выдаст новый.");
            _settings.Log("В клиенте: Настройки → Агенты → ввести этот код и адрес этой машины.");

            Tell(new AgentMoment
            {
                Kind = AgentMomentKind.CodeIssued,
                Text = offer.ForHumans,
            });
        }
        else if (Book.Thumbprints.Count == 0)
        {
            _settings.Log("Сопряжений нет и код не задан — принять никого не смогу.");
        }

        // Напоминание про брандмауэр даётся до первой неудачи, а не после. Слушающий
        // сокет открывается без прав, а снаружи до него никто не достучится — с нашей
        // стороны это выглядит совершенно исправно.
        _settings.Log("Если клиент не достучится: входящие на этот порт по умолчанию "
                      + "заблокированы Windows. Разрешить их может администратор — "
                      + "или используй режим дозвона: storm-agent connect <адрес>.");

        // Объявляет себя только тот, кто слушает: в режиме дозвона объявлять нечего,
        // и роль в обнаружении следует из выбранного направления, а не задаётся отдельно.
        var announcing = LocalDiscovery.AnnounceAsync(
            Environment.MachineName,
            _settings.Port,
            $"storm-agent/{ThisVersion}",
            Identity.Thumbprint,
            cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                _ = ServeAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _settings.Log("Остановлен.");
        }
        finally
        {
            listener.Stop();

            try
            {
                await announcing.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Дозванивается до клиента.
    /// </summary>
    /// <remarks>
    /// Режим для площадок, где прав нет: исходящие разрешены по умолчанию во всех трёх
    /// профилях Windows, и разрешение требуется не здесь, а на машине оператора, где
    /// оно уместно.
    /// </remarks>
    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
        ConnectAsync(host, port, null, cancellationToken);

    /// <summary>
    /// Дозванивается до клиента, предъявляя названный им код сопряжения.
    /// </summary>
    /// <param name="pairingCode">
    /// Код, который клиент показал при первом знакомстве. Пусто — сопряжение уже есть,
    /// и подтверждать нечего: дальше узнают по отпечатку.
    /// </param>
    public async Task ConnectAsync(
        string host,
        int port,
        string? pairingCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        _dialing = string.IsNullOrWhiteSpace(pairingCode)
            ? null
            : PairingOffer.For(pairingCode.Trim(), PairingCode.Lifetime);

        _settings.Log($"Дозваниваюсь до {host}:{port}. Отпечаток: {Identity.ThumbprintForHumans}");

        Tell(new AgentMoment
        {
            Kind = AgentMomentKind.Listening,
            Text = $"Дозваниваюсь до {host}:{port}.",
        });

        using var session = await SecureChannel
            .ConnectAsync(host, port, Options(), cancellationToken)
            .ConfigureAwait(false);

        Greet(session);

        await ConverseAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var session = await SecureChannel
                .AcceptAsync(client, Options(), cancellationToken)
                .ConfigureAwait(false);

            Greet(session);

            await ConverseAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            // Отказ уже объяснён собеседнику внутри рукопожатия. Здесь — для своего
            // оператора, который смотрит в консоль агента.
            _settings.Log($"Соединение отклонено: {ex.Message}");

            Tell(new AgentMoment { Kind = AgentMomentKind.Trouble, Text = $"Соединение отклонено: {ex.Message}" });
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            _settings.Log($"Соединение оборвалось: {ex.Message}");

            Tell(new AgentMoment { Kind = AgentMomentKind.Trouble, Text = $"Соединение оборвалось: {ex.Message}" });
        }
        finally
        {
            client.Dispose();
        }
    }

    private void Greet(SecureSession session)
    {
        if (session.WasPaired)
        {
            Book.Remember(session.Peer.Thumbprint, session.Peer.MachineName, session.Peer.Product);
            _settings.Log($"Сопряжение с {session.Peer.Describe()} выполнено. "
                          + $"Его отпечаток: {PeerIdentity.Group(session.Peer.Thumbprint)}");

            Tell(new AgentMoment
            {
                Kind = AgentMomentKind.PeerPaired,
                Peer = session.Peer.MachineName,
                Text = $"Сопряжение с {session.Peer.Describe()} выполнено.",
            });

            // Код гасится сразу после того, как им воспользовались по назначению.
            // Оставить его годным значило бы позволить услышавшему сопрячься вторым —
            // и оператор об этом не узнал бы: у него всё прошло успешно.
            if (_settings.Pairing?.Consume() == true)
            {
                _settings.Log("Код сопряжения погашен. Для следующего агента нужен новый код.");
            }
        }
        else
        {
            Book.Touch(session.Peer.Thumbprint);
            _settings.Log($"Подключился {session.Peer.Describe()} — сопряжение уже было, подтверждения не требуется.");

            Tell(new AgentMoment
            {
                Kind = AgentMomentKind.PeerArrived,
                Peer = session.Peer.MachineName,
                Text = $"Подключился {session.Peer.Describe()}.",
            });
        }
    }

    /// <summary>Разговор с опознанным собеседником, пока он не положит трубку.</summary>
    private async Task ConverseAsync(SecureSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await session.Channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (message is null)
            {
                _settings.Log($"{session.Peer.MachineName} отключился.");

                Tell(new AgentMoment
                {
                    Kind = AgentMomentKind.PeerLeft,
                    Peer = session.Peer.MachineName,
                    Text = $"{session.Peer.MachineName} отключился.",
                });

                return;
            }

            switch (message.Kind)
            {
                case MessageKind.Ping:
                    await session.Channel.SendAsync(
                        new ProtocolMessage { Kind = MessageKind.Pong, Exchange = message.Exchange },
                        cancellationToken).ConfigureAwait(false);
                    break;

                case MessageKind.StartTest:
                    await RunTestAsync(session, message, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageKind.Abort:
                    _settings.Log("Измерение прервано собеседником.");
                    break;

                default:
                    await session.Channel.SendAsync(
                        new ProtocolMessage
                        {
                            Kind = MessageKind.Refused,
                            Exchange = message.Exchange,
                            Reason = RefusalReason.Unsupported,
                            Explanation = $"Сообщение {message.Kind} агент не обрабатывает.",
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task RunTestAsync(
        SecureSession session,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        // Одно измерение за раз. Два теста одновременно мерили бы друг друга:
        // второй поток занимает тот же канал, и оба результата оказались бы
        // заниженными без всякого признака того, что это произошло.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            await session.Channel.SendAsync(
                new ProtocolMessage
                {
                    Kind = MessageKind.Refused,
                    Exchange = message.Exchange,
                    Reason = RefusalReason.Busy,
                    Explanation = "Агент уже занят другим измерением. Два теста одновременно "
                                  + "мерили бы друг друга.",
                },
                cancellationToken).ConfigureAwait(false);

            return;
        }

        try
        {
            _settings.Log($"Измерение {message.Request?.Kind} по просьбе {session.Peer.MachineName}…");

            var kind = message.Request?.Kind;

            Tell(new AgentMoment
            {
                Kind = AgentMomentKind.TestStarted,
                Peer = session.Peer.MachineName,
                Test = kind,
                Text = $"Измерение по просьбе {session.Peer.MachineName}.",
            });

            // Промежуточные снимки шли в никуда: консоли они не нужны, а больше их
            // некому было показать. Окну агента они и есть график — то же измерение,
            // которое видит оператор, видно и на той стороне.
            var progress = _settings.Watch is null
                ? null
                : new Progress<TestSnapshot>(sample => Tell(new AgentMoment
                {
                    Kind = AgentMomentKind.TestSample,
                    Peer = session.Peer.MachineName,
                    Test = kind,
                    Snapshot = sample,
                }));

            var snapshot = await TestConductor
                .ServeAsync(session, message, _ => null, progress, cancellationToken)
                .ConfigureAwait(false);

            if (snapshot is null)
            {
                return;
            }

            // Итог собеседнику отправляет тот, кто принимал поток, — это делает
            // сам TestConductor. Отправлять его отсюда значило бы прислать вторым
            // сообщением своё представление о том, сколько дошло, а его у отправителя
            // нет и быть не может.
            _settings.Log($"Измерение закончено: {snapshot.Mbps:0.0} Мбит/с, пакетов {snapshot.Packets}.");

            Tell(new AgentMoment
            {
                Kind = AgentMomentKind.TestFinished,
                Peer = session.Peer.MachineName,
                Test = kind,
                Snapshot = snapshot,
                Text = $"Измерение закончено: {snapshot.Mbps:0.0} Мбит/с.",
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Рассказывает окну о происшедшем.
    /// </summary>
    /// <remarks>
    /// Беда в рисовании не должна ронять измерение: агент — это точка измерения,
    /// а окно над ним — удобство. Поэтому исключение подписчика здесь гасится,
    /// а не пускается в разговор с собеседником.
    /// </remarks>
    private void Tell(AgentMoment moment)
    {
        if (_settings.Watch is not { } watch)
        {
            return;
        }

        try
        {
            watch(moment);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _settings.Log($"Окно не приняло событие: {ex.Message}");
        }
    }

    private ChannelOptions Options() => new()
    {
        Identity = Identity,
        KnownThumbprints = Book.Thumbprints,

        // Годен ли код ещё, решает само предложение: истёкший и использованный
        // не подставляются вовсе, и незнакомец получает отказ, а не догадку.
        // Порядок не случаен: чужой код при звонке весомее своего. Свой код агент
        // предъявлял бы собеседнику вместо названного им — и получил бы отказ.
        PairingCode = (_dialing ?? _settings.Pairing ?? _offered)?.CodeIfValid,
        ProductName = $"storm-agent/{ThisVersion}",
        Capabilities =
        [
            StormMachine.Protocol.Capabilities.TcpThroughput,
            StormMachine.Protocol.Capabilities.UdpQuality,
            StormMachine.Protocol.Capabilities.PrecisePacing,
        ],
    };

    private static string ThisVersion =>
        typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
