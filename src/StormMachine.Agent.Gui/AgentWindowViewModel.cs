using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Protocol;

namespace StormMachine.Agent.Gui;

/// <summary>Кто сопряжён с этим агентом.</summary>
public sealed record PeerRow(string Machine, string Product, string Paired, string Thumbprint);

/// <summary>Клиент, услышанный в сети: ему можно позвонить.</summary>
public sealed record ClientRow(string Machine, string Address, int Port)
{
    public string Caption => $"{Machine} — {Address}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Одно измерение, сделанное через этого агента.</summary>
public sealed record MeasurementRow(string At, string Peer, string Test, string Result);

/// <summary>
/// Окно агента: что с ним происходит прямо сейчас.
/// </summary>
/// <remarks>
/// Агент до И-26 был консолью, и человек на чужой площадке видел бегущие строки.
/// Для службы этого довольно, для человека — нет: он участвует в измерении
/// наравне с оператором и имеет право видеть то же самое. Отсюда состав окна:
/// отпечаток и код сопряжения (без них агента не подключить), кто сопряжён,
/// график идущего измерения и список сделанных.
/// <para>
/// Окно ничего не решает за агента: оно его показывает. Измерения проводит тот же
/// <see cref="AgentHost" />, что и в консоли, с теми же файлами личности
/// и сопряжений, — иначе это был бы второй агент, а не другой взгляд на первого.
/// </para>
/// </remarks>
public sealed partial class AgentWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько строк журнала держать на виду.</summary>
    /// <remarks>
    /// Окно агента работает сутками, и неограниченный журнал — это память, растущая
    /// линейно до самого конца. Прошлое остаётся в консоли и в файле, здесь нужно
    /// происходящее.
    /// </remarks>
    private const int JournalDepth = 200;

    private readonly CancellationTokenSource _stopping = new();

    private AgentHost? _host;
    private Task? _work;

    /// <summary>Идущий звонок: его и прерывает «отключиться».</summary>
    private CancellationTokenSource? _call;

    [ObservableProperty]
    private string _fingerprint = "…";

    [ObservableProperty]
    private string _stateLine = "Запускаюсь…";

    [ObservableProperty]
    private string _machine = Environment.MachineName;

    [ObservableProperty]
    private string? _code;

    [ObservableProperty]
    private string? _codeNote;

    [ObservableProperty]
    private string? _measuring;

    [ObservableProperty]
    private string _chartTitle = "Измерений ещё не было";

    // ------------------------------------------------------------------ звонок

    /// <summary>Адрес клиента: выбранный из найденных или набранный руками.</summary>
    [ObservableProperty]
    private string? _dialAddress;

    /// <summary>Код, который показал клиент. Нужен только при первом знакомстве.</summary>
    [ObservableProperty]
    private string? _dialCode;

    [ObservableProperty]
    private bool _isDialing;

    [ObservableProperty]
    private string? _dialState;

    [ObservableProperty]
    private bool _isSearching;

    public ObservableCollection<ClientRow> Clients { get; } = [];

    public ObservableCollection<string> Journal { get; } = [];

    public ObservableCollection<PeerRow> Peers { get; } = [];

    public ObservableCollection<MeasurementRow> Measurements { get; } = [];

    /// <summary>Мегабиты в секунду по снимкам текущего измерения — то, что рисует график.</summary>
    public List<double> ChartValues { get; } = [];

    /// <summary>График перерисовывается по событию: типы рисовалки в модель не попадают.</summary>
    public event EventHandler? ChartUpdated;

    public static string Promise =>
        "Агент отвечает только тем, с кем сопряжён, и только на то, о чём его просят. "
        + "Сам он ничего не измеряет и никуда не ходит.";

    /// <summary>Поднимает агента и начинает слушать.</summary>
    public void Start(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var port = Port(args);
        var pairing = args.Any(a => a is "--сопряжение" or "--pair") ? PairingOffer.Issue() : null;

        _host = new AgentHost(AgentSettings.Beside(port) with
        {
            Pairing = pairing,
            Interactive = true,
            Log = line => OnUi(() => Note(line)),
            Watch = moment => OnUi(() => Accept(moment)),
        });

        Fingerprint = _host.Identity.ThumbprintForHumans;
        StateLine = $"Слушаю порт {port.ToString(CultureInfo.InvariantCulture)}";

        ReadPeers();

        _work = Task.Run(async () =>
        {
            try
            {
                await _host.ListenAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                OnUi(() =>
                {
                    StateLine = "Не слушаю";
                    Note(ex.Message);
                });
            }
            catch (OperationCanceledException)
            {
                // Закрыли окно — это и есть остановка.
            }
        });
    }

    public void Stop()
    {
        _call?.Cancel();

        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }

    /// <summary>
    /// Ищет в сети клиентов, которые ждут звонка.
    /// </summary>
    /// <remarks>
    /// Клиент объявляет себя, только пока ждёт, — поэтому пустой список здесь значит
    /// ровно одно: на той стороне ожидание не запущено. Так и сказано: «ничего
    /// не найдено» без объяснения человек прочитает как поломку.
    /// </remarks>
    [RelayCommand]
    private async Task FindClientsAsync()
    {
        if (IsSearching)
        {
            return;
        }

        IsSearching = true;
        Clients.Clear();
        DialState = "Слушаю эфир…";

        try
        {
            var heard = await Task
                .Run(() => LocalDiscovery.BrowseAsync(TimeSpan.FromSeconds(6), CancellationToken.None))
                .ConfigureAwait(true);

            foreach (var client in heard.Where(p => p.IsClient))
            {
                Clients.Add(new ClientRow(client.MachineName, client.Address, client.Port));
            }

            if (Clients.Count == 0)
            {
                DialState = "Никто не ждёт звонка. Клиент объявляет себя, только пока ждёт: "
                            + "на его машине — Настройки → Агенты → ждать звонка. "
                            + "Если машины в разных подсетях, объявление сюда не дойдёт — "
                            + "наберите адрес руками.";
            }
            else
            {
                DialAddress ??= Clients[0].Address;
                DialState = $"Ждут звонка: {Clients.Count.ToString(CultureInfo.InvariantCulture)}.";
            }
        }
        catch (ProtocolException ex)
        {
            DialState = ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Звонит клиенту сам.
    /// </summary>
    /// <remarks>
    /// Направление, ради которого режим дозвона и существует: входящие на Windows
    /// закрыты по умолчанию, а правило их открыть требует прав администратора.
    /// Исходящие разрешены везде — поэтому на чужой площадке звонит агент.
    /// <para>
    /// Разговор длится, пока клиент не положит трубку: звонок — не одно действие,
    /// а состояние, и у него есть «отключиться».
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task DialAsync()
    {
        if (IsDialing || _host is null)
        {
            return;
        }

        var address = DialAddress?.Trim();

        if (string.IsNullOrWhiteSpace(address))
        {
            DialState = "Некуда звонить: выберите клиента из найденных или наберите адрес.";

            return;
        }

        var (host, port) = Split(address, SecureChannel.DefaultPort);

        _call = new CancellationTokenSource();
        IsDialing = true;
        DialState = $"Звоню {host}:{port.ToString(CultureInfo.InvariantCulture)}…";

        try
        {
            await _host.ConnectAsync(host, port, DialCode, _call.Token).ConfigureAwait(true);

            DialState = "Разговор окончен: клиент положил трубку.";
        }
        catch (ProtocolException ex)
        {
            DialState = ex.Message;
            Note(ex.Message);
        }
        catch (OperationCanceledException)
        {
            DialState = "Звонок прерван.";
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            DialState = $"Дозвониться не вышло: {ex.Message}";
            Note(DialState);
        }
        finally
        {
            _call?.Dispose();
            _call = null;
            IsDialing = false;

            // Код одноразовый: после разговора он погашен, и оставлять его в поле
            // значило бы предложить позвонить им второй раз.
            DialCode = null;

            ReadPeers();
        }
    }

    [RelayCommand]
    private void HangUp() => _call?.Cancel();

    /// <summary>Подставляет найденного в поле адреса — поправить его всё равно можно.</summary>
    [RelayCommand]
    private void UseClient(ClientRow? client)
    {
        if (client is null)
        {
            return;
        }

        DialAddress = client.Port == SecureChannel.DefaultPort
            ? client.Address
            : $"{client.Address}:{client.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Адрес с портом или без: «192.168.1.5» и «192.168.1.5:47820» — одно и то же поле.</summary>
    private static (string Host, int Port) Split(string address, int fallback)
    {
        var colon = address.LastIndexOf(':');

        if (colon > 0
            && int.TryParse(address[(colon + 1)..], CultureInfo.InvariantCulture, out var port)
            && port is > 0 and < 65536)
        {
            return (address[..colon], port);
        }

        return (address, fallback);
    }

    /// <summary>Копирует отпечаток: его сверяют на той стороне глазами, а не на слух.</summary>
    [RelayCommand]
    private async Task CopyFingerprintAsync()
    {
        await CopyAsync(Fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (Code is { Length: > 0 } code)
        {
            await CopyAsync(code).ConfigureAwait(true);
        }
    }

    private static async Task CopyAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                { MainWindow.Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Разбирает происшедшее в то, что видно на экране.
    /// </summary>
    /// <remarks>
    /// Открыто наружу намеренно: это вход окна, и проверки входят сюда же, не поднимая
    /// агента. Слушать настоящий порт в проверке значило бы требовать от неё свободного
    /// порта, прав и отсутствия чужого агента на той же машине.
    /// </remarks>
    public void Accept(AgentMoment moment)
    {
        switch (moment.Kind)
        {
            case AgentMomentKind.CodeIssued:
                Code = moment.Text;
                CodeNote = $"Годен {PairingCode.Lifetime.TotalMinutes:0} мин и только один раз. "
                           + "Введите его в клиенте вместе с адресом этой машины.";
                break;

            case AgentMomentKind.PeerPaired:
                Code = null;
                CodeNote = null;
                ReadPeers();
                break;

            case AgentMomentKind.PeerArrived:
                ReadPeers();
                break;

            case AgentMomentKind.TestStarted:
                ChartValues.Clear();
                ChartTitle = $"{Name(moment.Test)} — {moment.Peer}";
                Measuring = $"Идёт измерение: {Name(moment.Test)} по просьбе {moment.Peer}.";
                ChartUpdated?.Invoke(this, EventArgs.Empty);
                break;

            case AgentMomentKind.TestSample when moment.Snapshot is { } sample:
                ChartValues.Add(sample.Mbps);
                Measuring = $"{Name(moment.Test)}: {sample.Mbps:0.0} Мбит/с, "
                            + $"{sample.ElapsedSeconds:0} с от начала.";
                ChartUpdated?.Invoke(this, EventArgs.Empty);
                break;

            case AgentMomentKind.TestFinished when moment.Snapshot is { } done:
                Measuring = null;
                Measurements.Insert(0, new MeasurementRow(
                    moment.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    moment.Peer ?? "—",
                    Name(moment.Test),
                    Result(done)));
                break;

            default:
                break;
        }

        if (moment.Text is { Length: > 0 } && moment.Kind != AgentMomentKind.CodeIssued)
        {
            Note(moment.Text);
        }
    }

    /// <summary>Итог измерения словами продукта, а не набором полей.</summary>
    private static string Result(TestSnapshot done)
    {
        if (done.Failure is { Length: > 0 } failure)
        {
            return failure;
        }

        var text = $"{done.Mbps:0.0} Мбит/с";

        if (done.Packets > 0)
        {
            text += $", пакетов {done.Packets.ToString(CultureInfo.InvariantCulture)}";
        }

        if (done.Lost > 0)
        {
            text += $", потеряно {done.Lost.ToString(CultureInfo.InvariantCulture)}";
        }

        if (done.JitterMs > 0)
        {
            text += $", дрожание {done.JitterMs:0.0} мс";
        }

        return text;
    }

    private static string Name(TestKind? kind) => kind switch
    {
        TestKind.TcpThroughput => "Пропускная способность",
        TestKind.UdpQuality => "Качество канала",
        _ => "Измерение",
    };

    private void ReadPeers()
    {
        if (_host is null)
        {
            return;
        }

        Peers.Clear();

        foreach (var peer in _host.Book.All)
        {
            Peers.Add(new PeerRow(
                peer.MachineName,
                peer.Product,
                peer.PairedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
                PeerIdentity.Group(peer.Thumbprint)));
        }
    }

    private void Note(string line)
    {
        Journal.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {line}");

        while (Journal.Count > JournalDepth)
        {
            Journal.RemoveAt(Journal.Count - 1);
        }
    }

    /// <summary>Агент разговаривает из своих потоков; окно меняется только в своём.</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();

            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private static int Port(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--порт" or "--port"
                && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var port)
                && port is > 0 and < 65536)
            {
                return port;
            }
        }

        return SecureChannel.DefaultPort;
    }
}
