// Spike-02 — R-02 / R-10: сколько L2-данных доступно БЕЗ Npcap и без прав администратора,
// и можно ли выдержать точную темповку пакетов (нужна для UDP-jitter и throughput).
//
// Главная гипотеза: SendARP + GetIpNetTable из IPHLPAPI дают IP→MAC для всей подсети
// без драйвера захвата. Если да — Npcap перестаёт быть обязательным для инвентаризации
// и остаётся только для LLDP/CDP и пассивного анализа.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

static void H(string s) => Console.WriteLine($"\n=== {s} ===");
static string F(double v) => v.ToString("F3");

// ------------------------------------------------------------------ P/Invoke
[DllImport("iphlpapi.dll", ExactSpelling = true)]
static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

[DllImport("iphlpapi.dll", SetLastError = true)]
static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
static extern uint TimeBeginPeriod(uint uPeriod);

[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
static extern uint TimeEndPeriod(uint uPeriod);

static string Mac(byte[] b, int len) =>
    len <= 0 ? "-" : string.Join(":", b.Take(len).Select(x => x.ToString("X2")));

string gw = args.Length > 0 ? args[0] : "192.168.200.1";
var parts = gw.Split('.');
string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";

// ------------------------------------------------------- A. SendARP без admin
H("A. SendARP (IPHLPAPI) — резолв IP→MAC БЕЗ Npcap и БЕЗ admin");
{
    var mac = new byte[6];
    uint len = 6;
    var ip = IPAddress.Parse(gw);
    uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
    var sw = Stopwatch.StartNew();
    int rc = SendARP(dest, 0, mac, ref len);
    sw.Stop();
    Console.WriteLine(rc == 0
        ? $"Шлюз {gw} → MAC {Mac(mac, (int)len)}   ({F(sw.Elapsed.TotalMilliseconds)} мс)  ✔ РАБОТАЕТ"
        : $"SendARP вернул код {rc} — не сработало");
}

// ------------------------------------------------- B. ARP-таблица целиком
H("B. GetIpNetTable — чтение ARP-кэша ОС целиком");
var arpCache = new Dictionary<string, string>();
{
    int size = 0;
    GetIpNetTable(IntPtr.Zero, ref size, false);
    IntPtr buf = Marshal.AllocHGlobal(size);
    try
    {
        int rc = GetIpNetTable(buf, ref size, false);
        if (rc == 0)
        {
            int n = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MibIpNetRow>();
            Console.WriteLine($"Записей в ARP-таблице: {n}");
            for (int i = 0; i < n; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(buf + 4 + i * rowSize);
                string ip = new IPAddress(BitConverter.GetBytes(row.Addr)).ToString();
                if (row.PhysAddrLen > 0) arpCache[ip] = Mac(row.PhysAddr, row.PhysAddrLen);
            }
            foreach (var kv in arpCache.Take(10)) Console.WriteLine($"  {kv.Key,-16} {kv.Value}");
            if (arpCache.Count > 10) Console.WriteLine($"  ... ещё {arpCache.Count - 10}");
        }
        else Console.WriteLine($"GetIpNetTable вернул {rc}");
    }
    finally { Marshal.FreeHGlobal(buf); }
}

// ----------------------------------- C. Полная инвентаризация /24 без драйвера
H("C. ИНВЕНТАРИЗАЦИЯ /24 БЕЗ ДРАЙВЕРА: ping-sweep → SendARP → MAC для каждого живого");
{
    var swAll = Stopwatch.StartNew();
    var alive = new System.Collections.Concurrent.ConcurrentBag<string>();
    var sem = new SemaphoreSlim(256);
    await Task.WhenAll(Enumerable.Range(1, 254).Select(async i =>
    {
        await sem.WaitAsync();
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(IPAddress.Parse(prefix + i), 800, new byte[32]);
            if (r.Status == IPStatus.Success) alive.Add(prefix + i);
        }
        catch { }
        finally { sem.Release(); }
    }));
    double tPing = swAll.Elapsed.TotalSeconds;

    var swArp = Stopwatch.StartNew();
    int withMac = 0;
    var results = new List<(string ip, string mac)>();
    foreach (var ip in alive.OrderBy(x => int.Parse(x.Split('.')[3])))
    {
        var mac = new byte[6]; uint len = 6;
        uint dest = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
        string m = SendARP(dest, 0, mac, ref len) == 0 ? Mac(mac, (int)len) : "-";
        if (m != "-") withMac++;
        results.Add((ip, m));
    }
    swArp.Stop();

    Console.WriteLine($"Живых узлов: {alive.Count} (ping-sweep {F(tPing)} с)");
    Console.WriteLine($"MAC получен для: {withMac} из {alive.Count} (SendARP {F(swArp.Elapsed.TotalSeconds)} с)");
    Console.WriteLine($"ИТОГО инвентаризация /24 с MAC-адресами: {F(tPing + swArp.Elapsed.TotalSeconds)} с, БЕЗ драйвера и БЕЗ admin");
    foreach (var (ip, m) in results.Take(12))
    {
        string oui = m == "-" ? "" : m[..8];
        Console.WriteLine($"  {ip,-16} {m,-18} OUI={oui}");
    }
    if (results.Count > 12) Console.WriteLine($"  ... ещё {results.Count - 12}");
}

// -------------------------------------------------- D. Raw socket без admin
H("D. RAW SOCKET ICMP БЕЗ ADMIN — ожидаем отказ");
{
    try
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        Console.WriteLine("Raw socket СОЗДАН — неожиданно, значит права есть");
    }
    catch (SocketException e)
    {
        Console.WriteLine($"Raw socket отклонён: {e.SocketErrorCode} ({e.Message})");
        Console.WriteLine("→ Подтверждено: свой ICMP на raw-сокетах требует admin. Идём через IcmpSendEcho2.");
    }
}

// -------------------------------------------- E. Точность темповки пакетов
H("E. ТОЧНОСТЬ ТЕМПОВКИ ПАКЕТОВ (нужна для UDP-jitter и throughput)");
static (double p50, double p95, double max) PaceTest(Func<double, Action> waiter, double targetMs, int n)
{
    var errs = new List<double>();
    var wait = waiter(targetMs);
    for (int i = 0; i < n; i++)
    {
        var sw = Stopwatch.StartNew();
        wait();
        errs.Add(Math.Abs(sw.Elapsed.TotalMilliseconds - targetMs));
    }
    var a = errs.ToArray(); Array.Sort(a);
    return (a[a.Length / 2], a[(int)(a.Length * 0.95)], a[^1]);
}

double target = 1.0;
Action sleepW = () => Thread.Sleep(1);
Action spinW = () =>
{
    long ticks = (long)(target * Stopwatch.Frequency / 1000.0);
    long start = Stopwatch.GetTimestamp();
    var sp = new SpinWait();
    while (Stopwatch.GetTimestamp() - start < ticks) sp.SpinOnce(-1);
};
Action hybridW = () =>
{
    long ticks = (long)(target * Stopwatch.Frequency / 1000.0);
    long start = Stopwatch.GetTimestamp();
    long spinFrom = ticks - (long)(0.3 * Stopwatch.Frequency / 1000.0);
    if (spinFrom > 0) Thread.Sleep(0);
    while (Stopwatch.GetTimestamp() - start < ticks) Thread.SpinWait(20);
};

static (double p50, double p95, double max) Run(Action w, double t, int n)
{
    var errs = new List<double>();
    for (int i = 0; i < n; i++)
    {
        var sw = Stopwatch.StartNew();
        w();
        errs.Add(Math.Abs(sw.Elapsed.TotalMilliseconds - t));
    }
    var a = errs.ToArray(); Array.Sort(a);
    return (a[a.Length / 2], a[(int)(a.Length * 0.95)], a[^1]);
}

Console.WriteLine("Цель — выдержать интервал 1.000 мс. Ошибка (модуль отклонения):");
var r1 = Run(sleepW, target, 300);
Console.WriteLine($"  Thread.Sleep(1)              : p50 {F(r1.p50)}  p95 {F(r1.p95)}  max {F(r1.max)} мс");

TimeBeginPeriod(1);
var r2 = Run(sleepW, target, 300);
Console.WriteLine($"  Thread.Sleep(1) + timeBeginPeriod(1): p50 {F(r2.p50)}  p95 {F(r2.p95)}  max {F(r2.max)} мс");
TimeEndPeriod(1);

var r3 = Run(spinW, target, 300);
Console.WriteLine($"  SpinWait (жжём CPU)          : p50 {F(r3.p50)}  p95 {F(r3.p95)}  max {F(r3.max)} мс");

var r4 = Run(hybridW, target, 300);
Console.WriteLine($"  Гибрид Sleep(0)+SpinWait     : p50 {F(r4.p50)}  p95 {F(r4.p95)}  max {F(r4.max)} мс");

// ------------------------------------------ F. Приоритет потока и выбросы
H("F. ВЛИЯНИЕ ПРИОРИТЕТА ПОТОКА НА ВЫБРОСЫ RTT");
{
    static (double p50, double p95, double p99, double max) PingRun(IPAddress ip, int n)
    {
        using var ping = new Ping();
        var buf = new byte[32];
        var xs = new List<double>();
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = ping.Send(ip, 1000, buf);
            sw.Stop();
            if (r.Status == IPStatus.Success) xs.Add(sw.Elapsed.TotalMilliseconds);
            Thread.Sleep(10);
        }
        var a = xs.ToArray(); Array.Sort(a);
        return (a[a.Length / 2], a[(int)(a.Length * 0.95)], a[(int)(a.Length * 0.99)], a[^1]);
    }

    var ip = IPAddress.Parse(gw);
    var normal = PingRun(ip, 200);
    Console.WriteLine($"  Обычный приоритет : p50 {F(normal.p50)}  p95 {F(normal.p95)}  p99 {F(normal.p99)}  max {F(normal.max)} мс");

    var old = Process.GetCurrentProcess().PriorityClass;
    try
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        TimeBeginPeriod(1);
        var high = PingRun(ip, 200);
        Console.WriteLine($"  High + timeBeginPeriod: p50 {F(high.p50)}  p95 {F(high.p95)}  p99 {F(high.p99)}  max {F(high.max)} мс");
        Console.WriteLine($"  → улучшение p99: {F(normal.p99 - high.p99)} мс");
    }
    catch (Exception e) { Console.WriteLine($"  Не удалось поднять приоритет: {e.Message}"); }
    finally
    {
        TimeEndPeriod(1);
        try { Process.GetCurrentProcess().PriorityClass = old; } catch { }
        Thread.CurrentThread.Priority = ThreadPriority.Normal;
    }
}

// --------------------------------------------------- G. Интерфейсы и маршруты
H("G. СЕТЕВЫЕ ИНТЕРФЕЙСЫ (NetworkInterface, без admin)");
foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
             .Where(x => x.OperationalStatus == OperationalStatus.Up
                      && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
{
    var p = ni.GetIPProperties();
    var v4 = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
    var gws = string.Join(",", p.GatewayAddresses.Select(g => g.Address.ToString()));
    var dns = string.Join(",", p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()));
    Console.WriteLine($"  {ni.Name}");
    Console.WriteLine($"      тип={ni.NetworkInterfaceType} скорость={(ni.Speed > 0 ? ni.Speed / 1_000_000 + " Мбит/с" : "?")} MAC={ni.GetPhysicalAddress()}");
    Console.WriteLine($"      IPv4={v4?.Address} /{v4?.PrefixLength} шлюз=[{gws}] DNS=[{dns}]");
}

Console.WriteLine("\n=== ГОТОВО ===");

// --- типы объявляются после top-level statements ---
[StructLayout(LayoutKind.Sequential)]
struct MibIpNetRow
{
    public int Index;
    public int PhysAddrLen;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
    public uint Addr;
    public int Type;   // 1=other 2=invalid 3=dynamic 4=static
}
