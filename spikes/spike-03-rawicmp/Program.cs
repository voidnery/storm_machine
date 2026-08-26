// Spike-03 — R-01 / R-10: работает ли СОБСТВЕННЫЙ ICMP на raw-сокетах без прав администратора.
//
// Если да — мы получаем:
//   • свой ID/seq в пакете → можно слать пробы параллельно и сопоставлять ответы;
//   • свой timestamp в payload → RTT считается по данным пакета, а не по коду вокруг вызова;
//   • параллельный traceroute (все TTL разом) вместо последовательного;
//   • полный контроль над размером, паттерном и флагами.
// Если нет — остаёмся на IcmpSendEcho2 (System.Net.NetworkInformation.Ping).

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

static void H(string s) => Console.WriteLine($"\n=== {s} ===");
static string F(double v) => v.ToString("F3");

static ushort Checksum(ReadOnlySpan<byte> data)
{
    int sum = 0;
    for (int i = 0; i + 1 < data.Length; i += 2) sum += (data[i] << 8) | data[i + 1];
    if ((data.Length & 1) != 0) sum += data[^1] << 8;
    while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
    return (ushort)~sum;
}

// ICMP echo request: type=8, code=0, checksum, id, seq, payload(timestamp)
static byte[] BuildEcho(ushort id, ushort seq, int payloadSize, long timestamp)
{
    var pkt = new byte[8 + payloadSize];
    pkt[0] = 8; pkt[1] = 0;
    pkt[4] = (byte)(id >> 8); pkt[5] = (byte)id;
    pkt[6] = (byte)(seq >> 8); pkt[7] = (byte)seq;
    if (payloadSize >= 8) BitConverter.TryWriteBytes(pkt.AsSpan(8), timestamp);
    for (int i = 16; i < pkt.Length; i++) pkt[i] = (byte)(i & 0xFF);
    ushort ck = Checksum(pkt);
    pkt[2] = (byte)(ck >> 8); pkt[3] = (byte)ck;
    return pkt;
}

string gw = args.Length > 0 ? args[0] : "192.168.200.1";
var gwIp = IPAddress.Parse(gw);
ushort myId = (ushort)(Environment.ProcessId & 0xFFFF);

// ------------------------------------------------- A. Raw ICMP send/receive
H("A. RAW ICMP — реально ли ОТПРАВИТЬ и ПОЛУЧИТЬ без admin");
bool rawWorks = false;
{
    try
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        s.ReceiveTimeout = 2000;
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        var pkt = BuildEcho(myId, 1, 32, Stopwatch.GetTimestamp());
        var sw = Stopwatch.StartNew();
        int sent = s.SendTo(pkt, new IPEndPoint(gwIp, 0));
        Console.WriteLine($"SendTo вернул {sent} байт — отправка прошла");
        var buf = new byte[1500];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int got = s.ReceiveFrom(buf, ref from);
        sw.Stop();
        int ihl = (buf[0] & 0x0F) * 4;
        byte type = buf[ihl];
        ushort rid = (ushort)((buf[ihl + 4] << 8) | buf[ihl + 5]);
        Console.WriteLine($"Получено {got} байт от {from}, ICMP type={type}, id={rid} (наш id={myId})");
        Console.WriteLine($"RTT по Stopwatch: {F(sw.Elapsed.TotalMilliseconds)} мс");
        rawWorks = type == 0 && rid == myId;
        Console.WriteLine(rawWorks
            ? "✔ RAW ICMP ПОЛНОСТЬЮ РАБОТАЕТ БЕЗ ADMIN — свой движок ICMP возможен"
            : "Ответ пришёл, но не наш echo reply");
    }
    catch (SocketException e)
    {
        Console.WriteLine($"✘ Отказ: {e.SocketErrorCode} — {e.Message}");
        Console.WriteLine("→ Остаёмся на IcmpSendEcho2 (System.Net.NetworkInformation.Ping)");
    }
}

// ---------------------------------- B. Точность RTT: timestamp внутри пакета
if (rawWorks)
{
    H("B. RTT ПО TIMESTAMP ВНУТРИ ПАКЕТА vs ПО КОДУ ВОКРУГ ВЫЗОВА");
    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
    s.ReceiveTimeout = 1000;
    s.Bind(new IPEndPoint(IPAddress.Any, 0));
    var buf = new byte[1500];
    var outer = new List<double>();
    var inner = new List<double>();
    for (int i = 0; i < 200; i++)
    {
        long ts = Stopwatch.GetTimestamp();
        var pkt = BuildEcho(myId, (ushort)(i + 100), 32, ts);
        var swOuter = Stopwatch.StartNew();
        s.SendTo(pkt, new IPEndPoint(gwIp, 0));
        try
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int got = s.ReceiveFrom(buf, ref from);
            long now = Stopwatch.GetTimestamp();
            swOuter.Stop();
            int ihl = (buf[0] & 0x0F) * 4;
            if (buf[ihl] == 0 && got >= ihl + 16)
            {
                long echoed = BitConverter.ToInt64(buf, ihl + 8);
                inner.Add((now - echoed) * 1000.0 / Stopwatch.Frequency);
                outer.Add(swOuter.Elapsed.TotalMilliseconds);
            }
        }
        catch (SocketException) { }
        Thread.Sleep(10);
    }
    static (double p50, double p95, double p99) P(List<double> xs)
    { var a = xs.ToArray(); Array.Sort(a); return (a[a.Length / 2], a[(int)(a.Length * .95)], a[(int)(a.Length * .99)]); }
    if (inner.Count > 10)
    {
        var pi = P(inner); var po = P(outer);
        Console.WriteLine($"Ответов: {inner.Count}/200");
        Console.WriteLine($"RTT по timestamp в пакете: p50 {F(pi.p50)}  p95 {F(pi.p95)}  p99 {F(pi.p99)} мс");
        Console.WriteLine($"RTT по коду вокруг вызова: p50 {F(po.p50)}  p95 {F(po.p95)}  p99 {F(po.p99)} мс");
        Console.WriteLine($"→ разница p50: {F(po.p50 - pi.p50)} мс");
    }
}

// ------------------------------------ C. Параллельный traceroute на raw ICMP
if (rawWorks)
{
    H("C. ПАРАЛЛЕЛЬНЫЙ TRACEROUTE (все TTL одним залпом) до 1.1.1.1");
    var target = IPAddress.Parse("1.1.1.1");
    const int maxTtl = 12;
    var hops = new string[maxTtl + 1];
    var times = new double[maxTtl + 1];
    var sockets = new Socket[maxTtl + 1];
    var swAll = Stopwatch.StartNew();
    try
    {
        for (int ttl = 1; ttl <= maxTtl; ttl++)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            s.ReceiveTimeout = 1500;
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            sockets[ttl] = s;
            s.SendTo(BuildEcho(myId, (ushort)(1000 + ttl), 32, Stopwatch.GetTimestamp()),
                     new IPEndPoint(target, 0));
        }
        Parallel.For(1, maxTtl + 1, ttl =>
        {
            var buf = new byte[1500];
            var sw = Stopwatch.StartNew();
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                sockets[ttl].ReceiveFrom(buf, ref from);
                sw.Stop();
                hops[ttl] = ((IPEndPoint)from).Address.ToString();
                times[ttl] = sw.Elapsed.TotalMilliseconds;
            }
            catch (SocketException) { hops[ttl] = "*"; }
        });
        swAll.Stop();
        for (int ttl = 1; ttl <= maxTtl; ttl++)
        {
            Console.WriteLine($"  ttl={ttl,2}  {hops[ttl] ?? "*",-16} {F(times[ttl]),8} мс");
            if (hops[ttl] == target.ToString()) break;
        }
        Console.WriteLine($"→ Весь traceroute за {F(swAll.Elapsed.TotalMilliseconds)} мс (последовательный в spike-01 занял ~1.4 с)");
    }
    finally { foreach (var s in sockets) s?.Dispose(); }
}

// ----------------------------------------------- D. UDP-джиттер по петле
H("D. UDP: точность темповки и джиттер (петля через шлюз недоступна — меряем loopback)");
{
    using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    rx.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    rx.ReceiveTimeout = 500;
    var rxEp = (IPEndPoint)rx.LocalEndPoint!;
    using var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    var sendGaps = new List<double>();
    var transit = new List<double>();
    var buf = new byte[256];
    double targetGap = 1.0; // 1 мс между пакетами = 1000 pps
    long last = 0;
    for (int i = 0; i < 500; i++)
    {
        long ticks = (long)(targetGap * Stopwatch.Frequency / 1000.0);
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks) Thread.SpinWait(20);

        long now = Stopwatch.GetTimestamp();
        if (last != 0) sendGaps.Add((now - last) * 1000.0 / Stopwatch.Frequency);
        last = now;

        var pkt = new byte[64];
        BitConverter.TryWriteBytes(pkt.AsSpan(0), now);
        tx.SendTo(pkt, rxEp);
        try
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int got = rx.ReceiveFrom(buf, ref from);
            long recvNow = Stopwatch.GetTimestamp();
            long sentAt = BitConverter.ToInt64(buf, 0);
            transit.Add((recvNow - sentAt) * 1000.0 / Stopwatch.Frequency);
        }
        catch (SocketException) { }
    }
    static (double p50, double p95, double max) P2(List<double> xs)
    { var a = xs.ToArray(); Array.Sort(a); return (a[a.Length / 2], a[(int)(a.Length * .95)], a[^1]); }
    double jitter = 0;
    for (int i = 1; i < transit.Count; i++) jitter += (Math.Abs(transit[i] - transit[i - 1]) - jitter) / 16.0;
    var g = P2(sendGaps); var t = P2(transit);
    Console.WriteLine($"Интервал отправки (цель 1.000 мс): p50 {F(g.p50)}  p95 {F(g.p95)}  max {F(g.max)} мс");
    Console.WriteLine($"Транзит loopback              : p50 {F(t.p50)}  p95 {F(t.p95)}  max {F(t.max)} мс");
    Console.WriteLine($"Собственный джиттер стека (RFC3550): {F(jitter)} мс  ← это наш ШУМОВОЙ ПОЛ для UDP-тестов");
}

// -------------------------------------------------------- E. TCP-connect и DNS
H("E. TCP-CONNECT И DNS — тайминги");
{
    static async Task<double> TcpConnect(string host, int port)
    {
        var sw = Stopwatch.StartNew();
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await s.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(3)); return sw.Elapsed.TotalMilliseconds; }
        catch { return -1; }
    }
    foreach (var (h, p) in new[] { ("1.1.1.1", 443), ("8.8.8.8", 53), (gw, 80) })
    {
        double ms = await TcpConnect(h, p);
        Console.WriteLine($"  TCP {h}:{p,-4} → {(ms < 0 ? "недоступен" : F(ms) + " мс")}");
    }
    var swd = Stopwatch.StartNew();
    try
    {
        var e = await Dns.GetHostEntryAsync("example.com");
        Console.WriteLine($"  DNS example.com → {string.Join(",", e.AddressList.Take(2).Select(a => a.ToString()))} за {F(swd.Elapsed.TotalMilliseconds)} мс");
    }
    catch (Exception ex) { Console.WriteLine($"  DNS: {ex.GetType().Name}"); }
}

Console.WriteLine("\n=== ГОТОВО ===");
