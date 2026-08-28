using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace Spike08;

/// <summary>
/// Коммутатор-дублёр.
/// </summary>
/// <remarks>
/// Появился как половина спайка — чтобы проверить обрезку на обеих сторонах BER.
/// Дорос до стенда по необходимости: управляемого оборудования под рукой может
/// не быть, а проверять чтение <c>ifTable</c>, соседей и таблицы пересылки на чём-то
/// надо. Это <b>испытательная оснастка, а не часть продукта</b>: она живёт в spikes
/// и в поставку не входит.
/// <para>
/// Счётчики растут от времени работы дублёра, а не стоят на месте: иначе измерение
/// нагрузки давало бы ноль, и проверить его было бы нечем. Порт 3 нарочно сыплет
/// ошибками, порт 4 включён без линка, порт 5 выключен администратором — три случая,
/// которые продукт обязан различать.
/// </para>
/// </remarks>
internal sealed class Device(int port, string community)
{
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private readonly OctetString _community = new(community, Encoding.UTF8);

    /// <summary>Порты дублёра: индекс, имя, подпись, скорость, состояние.</summary>
    private static readonly (int Index, string Name, string Descr, string Alias, long Speed, int Admin, int Oper)[]
        Ports =
        [
            (1, "lo", "Software Loopback", "", 0, 1, 1),
            (2, "Gi0/1", "GigabitEthernet0/1", "к ядру sw-core-01", 1_000_000_000, 1, 1),
            (3, "Gi0/2", "GigabitEthernet0/2", "серверная, стойка 2", 1_000_000_000, 1, 1),
            (4, "Gi0/3", "GigabitEthernet0/3", "переговорная", 1_000_000_000, 1, 2),
            (5, "Gi0/4", "GigabitEthernet0/4", "резерв", 1_000_000_000, 2, 2),
        ];

    /// <summary>Что видно в таблице пересылки: адрес, порт моста.</summary>
    private static readonly (string Mac, int BridgePort)[] Learned =
    [
        ("00-1B-21-3C-4D-5E", 1),
        ("00-1B-21-3C-4D-5F", 1),
        ("00-1B-21-3C-4D-60", 1),
        ("00-1B-21-3C-4D-61", 1),
        ("00-1B-21-3C-4D-62", 1),
        ("A4-BB-6D-11-22-33", 2),
        ("B8-27-EB-AA-BB-CC", 2),
    ];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        var registry = new UserRegistry();

        Console.WriteLine($"Коммутатор-дублёр слушает 0.0.0.0:{port.ToString(CultureInfo.InvariantCulture)}, "
                          + $"сообщество «{community}». Остановить — Ctrl+C.");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;

            try
            {
                datagram = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var request = MessageFactory.ParseMessages(datagram.Buffer, registry)[0];

                // Чужое сообщество остаётся без ответа, а не получает отказ:
                // так предписывает RFC 3414 §3.2, и продукт обязан уметь работать
                // именно с таким поведением — молчание неотличимо от выключенного SNMP.
                if (!request.Community().Equals(_community))
                {
                    continue;
                }

                var answer = Answer(request);
                var bytes = answer.ToBytes();

                await socket.SendAsync(bytes, bytes.Length, datagram.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Lextm.SharpSnmpLib.SnmpException or ArgumentException)
            {
                // Мусор в порту — не повод падать: устройство продолжает работать.
            }
        }
    }

    private ISnmpMessage Answer(ISnmpMessage request)
    {
        var mib = Snapshot();
        var order = mib.Keys.OrderBy(o => o, OidOrder.Instance).ToList();
        var answers = new List<Variable>();

        foreach (var asked in request.Pdu().Variables)
        {
            var oid = asked.Id.ToString();

            if (request.Pdu().TypeCode is SnmpType.GetNextRequestPdu or SnmpType.GetBulkRequestPdu)
            {
                var next = order.FirstOrDefault(o => OidOrder.Instance.Compare(o, oid) > 0);

                answers.Add(next is null
                    ? new Variable(asked.Id, new EndOfMibView())
                    : new Variable(new ObjectIdentifier(next), mib[next]));
            }
            else
            {
                answers.Add(new Variable(
                    asked.Id,
                    mib.TryGetValue(oid, out var value) ? value : new NoSuchObject()));
            }
        }

        return new ResponseMessage(
            request.RequestId(),
            VersionCode.V2,
            request.Community(),
            ErrorCode.NoError,
            0,
            answers);
    }

    /// <summary>Состояние устройства на текущий момент.</summary>
    private Dictionary<string, ISnmpData> Snapshot()
    {
        var seconds = _since.Elapsed.TotalSeconds;

        var mib = new Dictionary<string, ISnmpData>(StringComparer.Ordinal)
        {
            ["1.3.6.1.2.1.1.1.0"] = Str("Storm Machine simulated switch, 5 ports, firmware 1.0"),
            ["1.3.6.1.2.1.1.2.0"] = new ObjectIdentifier("1.3.6.1.4.1.99999.1"),
            ["1.3.6.1.2.1.1.3.0"] = new TimeTicks((uint)(seconds * 100)),
            ["1.3.6.1.2.1.1.4.0"] = Str("noc@example.test"),
            ["1.3.6.1.2.1.1.5.0"] = Str("sw-spike-01"),
            ["1.3.6.1.2.1.1.6.0"] = Str("стенд, стол у окна"),

            // 2 — работает вторым уровнем. Роль всё равно решится по наличию
            // таблицы пересылки: заявленные услуги — только подсказка.
            ["1.3.6.1.2.1.1.7.0"] = new Integer32(2),
        };

        foreach (var (index, name, descr, alias, speed, admin, oper) in Ports)
        {
            var i = index.ToString(CultureInfo.InvariantCulture);

            mib[$"1.3.6.1.2.1.2.2.1.2.{i}"] = Str(descr);
            mib[$"1.3.6.1.2.1.2.2.1.3.{i}"] = new Integer32(index == 1 ? 24 : 6);
            mib[$"1.3.6.1.2.1.2.2.1.4.{i}"] = new Integer32(1500);
            mib[$"1.3.6.1.2.1.2.2.1.5.{i}"] = new Gauge32((uint)Math.Min(speed, uint.MaxValue));
            mib[$"1.3.6.1.2.1.2.2.1.6.{i}"] = Str(Mac(index));
            mib[$"1.3.6.1.2.1.2.2.1.7.{i}"] = new Integer32(admin);
            mib[$"1.3.6.1.2.1.2.2.1.8.{i}"] = new Integer32(oper);

            mib[$"1.3.6.1.2.1.31.1.1.1.1.{i}"] = Str(name);
            mib[$"1.3.6.1.2.1.31.1.1.1.15.{i}"] = new Gauge32((uint)(speed / 1_000_000));
            mib[$"1.3.6.1.2.1.31.1.1.1.18.{i}"] = Str(alias);

            Counters(mib, index, seconds, oper == 1);
        }

        Bridge(mib);
        Lldp(mib);

        return mib;
    }

    /// <summary>
    /// Счётчики трафика и ошибок.
    /// </summary>
    /// <remarks>
    /// Растут пропорционально времени работы, у каждого порта со своей скоростью.
    /// Порт 3 сыплет ошибками — примерно одна на три тысячи кадров: столько даёт
    /// умирающий патч-корд, и именно это продукт должен показать долей, а не штуками.
    /// </remarks>
    private static void Counters(Dictionary<string, ISnmpData> mib, int index, double seconds, bool live)
    {
        var i = index.ToString(CultureInfo.InvariantCulture);

        var bytesPerSecond = live ? index * 1_250_000L : 0;
        var packetsPerSecond = live ? index * 900L : 0;

        var inOctets = (long)(bytesPerSecond * seconds);
        var outOctets = (long)(bytesPerSecond * 0.4 * seconds);
        var inPackets = (long)(packetsPerSecond * seconds);
        var outPackets = (long)(packetsPerSecond * 0.6 * seconds);

        var inErrors = index == 3 ? inPackets / 3_000 : 0;
        var outErrors = index == 3 ? outPackets / 9_000 : 0;

        mib[$"1.3.6.1.2.1.2.2.1.10.{i}"] = new Counter32((uint)(inOctets % uint.MaxValue));
        mib[$"1.3.6.1.2.1.2.2.1.11.{i}"] = new Counter32((uint)(inPackets % uint.MaxValue));
        mib[$"1.3.6.1.2.1.2.2.1.13.{i}"] = new Counter32(0);
        mib[$"1.3.6.1.2.1.2.2.1.14.{i}"] = new Counter32((uint)inErrors);
        mib[$"1.3.6.1.2.1.2.2.1.16.{i}"] = new Counter32((uint)(outOctets % uint.MaxValue));
        mib[$"1.3.6.1.2.1.2.2.1.17.{i}"] = new Counter32((uint)(outPackets % uint.MaxValue));
        mib[$"1.3.6.1.2.1.2.2.1.19.{i}"] = new Counter32(0);
        mib[$"1.3.6.1.2.1.2.2.1.20.{i}"] = new Counter32((uint)outErrors);

        mib[$"1.3.6.1.2.1.31.1.1.1.6.{i}"] = new Counter64((ulong)inOctets);
        mib[$"1.3.6.1.2.1.31.1.1.1.7.{i}"] = new Counter64((ulong)inPackets);
        mib[$"1.3.6.1.2.1.31.1.1.1.10.{i}"] = new Counter64((ulong)outOctets);
        mib[$"1.3.6.1.2.1.31.1.1.1.11.{i}"] = new Counter64((ulong)outPackets);
    }

    /// <summary>Таблица пересылки и соответствие «порт моста → ifIndex».</summary>
    private static void Bridge(Dictionary<string, ISnmpData> mib)
    {
        for (var bridgePort = 1; bridgePort <= 4; bridgePort++)
        {
            mib[$"1.3.6.1.2.1.17.1.4.1.2.{bridgePort.ToString(CultureInfo.InvariantCulture)}"] =
                new Integer32(bridgePort + 1);
        }

        foreach (var (mac, bridgePort) in Learned)
        {
            var key = string.Join('.', mac.Split('-').Select(b => Convert.ToInt32(b, 16)));

            mib[$"1.3.6.1.2.1.17.4.3.1.2.{key}"] = new Integer32(bridgePort);
            mib[$"1.3.6.1.2.1.17.4.3.1.3.{key}"] = new Integer32(3);
        }
    }

    /// <summary>Один сосед на первом гигабитном порту.</summary>
    private static void Lldp(Dictionary<string, ISnmpData> mib)
    {
        // Индекс тройной: отметка времени, локальный порт, номер соседа.
        const string key = "0.2.1";

        mib[$"1.0.8802.1.1.2.1.4.1.1.5.{key}"] = Str("00-1C-0E-AA-BB-01");
        mib[$"1.0.8802.1.1.2.1.4.1.1.7.{key}"] = Str("Te1/0/24");
        mib[$"1.0.8802.1.1.2.1.4.1.1.8.{key}"] = Str("uplink to access sw-spike-01");
        mib[$"1.0.8802.1.1.2.1.4.1.1.9.{key}"] = Str("sw-core-01");
        mib[$"1.0.8802.1.1.2.1.4.1.1.10.{key}"] = Str("Core switch, firmware 4.2");
    }

    private static string Mac(int index) => $"00-50-56-00-00-{index:X2}";

    /// <summary>
    /// Строка устройства в UTF-8.
    /// </summary>
    /// <remarks>
    /// Явно, а не кодировкой по умолчанию: библиотека по умолчанию берёт ASCII,
    /// и «серверная, стойка 2» превратилась бы в ряд вопросительных знаков ещё
    /// на стороне дублёра. Настоящее оборудование пишет UTF-8, как предписывает
    /// SnmpAdminString (RFC 3411 §5), — дублёр обязан вести себя так же, иначе
    /// он проверял бы не то.
    /// </remarks>
    private static OctetString Str(string text) => new(text, Encoding.UTF8);
}

/// <summary>
/// Порядок узлов дерева.
/// </summary>
/// <remarks>
/// Числовой, а не строковый: строкой «14» меньше «2», и обход дерева ушёл бы
/// не туда. Настоящее оборудование отдаёт узлы числовым порядком, и дублёр обязан
/// вести себя так же — иначе он проверял бы не то.
/// </remarks>
internal sealed class OidOrder : IComparer<string>
{
    public static OidOrder Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static long[] Parse(string? oid) => oid is null
        ? []
        : [.. oid.Split('.').Select(p => long.TryParse(p, out var value) ? value : 0)];
}
