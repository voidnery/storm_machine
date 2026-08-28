using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace StormMachine.Snmp.UnitTests;

/// <summary>
/// Устройство-дублёр на петле.
/// </summary>
/// <remarks>
/// Разбор ответов проверить иначе нечем: он весь состоит из соглашений о том,
/// как в SNMP закодирована таблица, и ошибиться в них можно только на настоящих
/// байтах. Дублёр отвечает теми же байтами, что оборудование, и собирается той же
/// библиотекой — значит проверяется именно наш разбор, а не наши же представления
/// о нём.
/// <para>
/// Каждый набор ответов задаётся тестом целиком. Это нарочно: половина проверок
/// здесь про <b>неполные</b> устройства — без расширенной таблицы, без соседей,
/// с таблицей пересылки в другой ветке. Общий дублёр «как у хорошего коммутатора»
/// такие случаи скрыл бы.
/// </para>
/// </remarks>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Dictionary<string, ISnmpData> _mib;

    public FakeAgent(Dictionary<string, ISnmpData> mib)
    {
        _mib = mib;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => ServeAsync(_stop.Token));
    }

    public int Port { get; }

    /// <summary>Сколько запросов пришло — чтобы видеть, что лишних кругов нет.</summary>
    public int Requests { get; private set; }

    public static Dictionary<string, ISnmpData> System(string name = "sw-test", int services = 2) => new(
        StringComparer.Ordinal)
    {
        ["1.3.6.1.2.1.1.1.0"] = Text("Fake switch, 4 ports"),
        ["1.3.6.1.2.1.1.3.0"] = new TimeTicks(360_000),
        ["1.3.6.1.2.1.1.5.0"] = Text(name),
        ["1.3.6.1.2.1.1.6.0"] = Text("серверная, стойка 2"),
        ["1.3.6.1.2.1.1.7.0"] = new Integer32(services),
    };

    /// <summary>Строка в UTF-8: библиотека по умолчанию берёт ASCII и портит кириллицу.</summary>
    public static OctetString Text(string value) => new(value, Encoding.UTF8);

    public static string MacKey(string mac) =>
        string.Join('.', mac.Split('-').Select(b => Convert.ToInt32(b, 16).ToString(CultureInfo.InvariantCulture)));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Остановлен намеренно.
        }

        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var registry = new UserRegistry();
        var order = _mib.Keys.OrderBy(o => o, OidOrder.Instance).ToList();

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;

            try
            {
                datagram = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            Requests++;

            var request = MessageFactory.ParseMessages(datagram.Buffer, registry)[0];
            var answers = new List<Variable>();

            foreach (var asked in request.Pdu().Variables)
            {
                var oid = asked.Id.ToString();

                if (request.Pdu().TypeCode is SnmpType.GetNextRequestPdu or SnmpType.GetBulkRequestPdu)
                {
                    var next = order.FirstOrDefault(o => OidOrder.Instance.Compare(o, oid) > 0);

                    answers.Add(next is null
                        ? new Variable(asked.Id, new EndOfMibView())
                        : new Variable(new ObjectIdentifier(next), _mib[next]));
                }
                else
                {
                    answers.Add(new Variable(
                        asked.Id,
                        _mib.TryGetValue(oid, out var value) ? value : new NoSuchObject()));
                }
            }

            var response = new ResponseMessage(
                request.RequestId(),
                VersionCode.V2,
                request.Community(),
                ErrorCode.NoError,
                0,
                answers);

            var bytes = response.ToBytes();

            await _socket.SendAsync(bytes, bytes.Length, datagram.RemoteEndPoint).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Числовой порядок узлов.
    /// </summary>
    /// <remarks>
    /// Строкой «14» меньше «2», и обход дерева ушёл бы не туда. Настоящее
    /// оборудование отдаёт узлы числовым порядком — дублёр обязан вести себя так же,
    /// иначе он проверяет не то.
    /// </remarks>
    private sealed class OidOrder : IComparer<string>
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
}
