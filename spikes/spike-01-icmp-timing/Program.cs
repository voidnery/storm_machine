// Spike-01 — R-01 / R-10: точность ICMP и таймингов на .NET без прав администратора.
//
// Проверяем:
//   1. Работает ли ICMP (System.Net.NetworkInformation.Ping → IcmpSendEcho2) без admin.
//   2. Какова РЕАЛЬНАЯ разрешающая способность RTT из API (подозрение: целые миллисекунды).
//   3. Какова точность собственного замера через Stopwatch (суб-миллисекундная?).
//   4. Каков шум измерительного стека (GC, планировщик) — критерий §6: ≤20% измеряемой величины.
//   5. Доступен ли TTL-режим (traceroute) и DontFragment (PMTU) без admin.
//   6. Успеваем ли ping-sweep /24 за ≤5 с (NFR §6).

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

const int WarmUp = 20;

static void H(string s) => Console.WriteLine($"\n=== {s} ===");
static string F(double v) => v.ToString("F3");

static (double min, double max, double avg, double p50, double p95, double p99, double stddev) Stats(List<double> xs)
{
    var a = xs.ToArray();
    Array.Sort(a);
    double avg = a.Average();
    double sd = Math.Sqrt(a.Sum(x => (x - avg) * (x - avg)) / a.Length);
    double P(double q) => a[Math.Min(a.Length - 1, (int)Math.Ceiling(q * a.Length) - 1)];
    return (a[0], a[^1], avg, P(0.50), P(0.95), P(0.99), sd);
}

// RFC 3550 §6.4.1: J += (|D(i-1,i)| - J) / 16
static double Rfc3550Jitter(List<double> rtt)
{
    double j = 0;
    for (int i = 1; i < rtt.Count; i++)
        j += (Math.Abs(rtt[i] - rtt[i - 1]) - j) / 16.0;
    return j;
}

// ---------------------------------------------------------------- A. Окружение
H("A. ОКРУЖЕНИЕ");
bool elevated;
using (var wi = System.Security.Principal.WindowsIdentity.GetCurrent())
    elevated = new System.Security.Principal.WindowsPrincipal(wi)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
Console.WriteLine($"Runtime            : {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Права администратора: {(elevated ? "ЕСТЬ" : "НЕТ (это и проверяем)")}");
Console.WriteLine($"Server GC          : {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");
Console.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency:N0} тик/с  → шаг {F(1e9 / Stopwatch.Frequency)} нс");

// ---------------------------------------------------------- B. Шумовой пол таймера
H("B. ШУМОВОЙ ПОЛ ТАЙМЕРА (без сети)");
{
    var deltas = new List<double>(200_000);
    for (int i = 0; i < 200_000; i++)
    {
        long t1 = Stopwatch.GetTimestamp();
        long t2 = Stopwatch.GetTimestamp();
        deltas.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
    }
    var s = Stats(deltas);
    Console.WriteLine($"Стоимость чтения таймера: медиана {F(s.p50)} мкс, p99 {F(s.p99)} мкс, max {F(s.max)} мкс");

    // Насколько точно мы можем «подождать 1 мс» — это влияет на интервалы между пакетами
    var sleepErr = new List<double>();
    for (int i = 0; i < 200; i++)
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1);
        sleepErr.Add(sw.Elapsed.TotalMilliseconds - 1.0);
    }
    var ss = Stats(sleepErr);
    Console.WriteLine($"Ошибка Thread.Sleep(1): медиана +{F(ss.p50)} мс, p95 +{F(ss.p95)} мс, max +{F(ss.max)} мс");
}

// ------------------------------------------------------------------ C. Loopback
H("C. LOOPBACK PING — пол накладных расходов стека");
{
    var api = new List<double>();
    var sw_ = new List<double>();
    using var ping = new Ping();
    var buf = new byte[32];
    for (int i = 0; i < WarmUp + 200; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = ping.Send(IPAddress.Loopback, 1000, buf);
        sw.Stop();
        if (i < WarmUp) continue;
        if (r.Status == IPStatus.Success)
        {
            api.Add(r.RoundtripTime);
            sw_.Add(sw.Elapsed.TotalMilliseconds);
        }
    }
    var a = Stats(api);
    var b = Stats(sw_);
    Console.WriteLine($"API RoundtripTime : min {F(a.min)} avg {F(a.avg)} max {F(a.max)} мс   ← различимых значений: {api.Distinct().Count()}");
    Console.WriteLine($"Stopwatch         : min {F(b.min)} avg {F(b.avg)} p95 {F(b.p95)} max {F(b.max)} мс");
}

// ---------------------------------------------------------------- D. Шлюз, sync
string gw = args.Length > 0 ? args[0] : "192.168.200.1";
int N = args.Length > 1 ? int.Parse(args[1]) : 300;
var gwIp = IPAddress.Parse(gw);

H($"D. ПИНГ ШЛЮЗА {gw} — синхронный, N={N}, интервал 20 мс");
long gcPauseBefore = GC.GetTotalPauseDuration().Ticks;
int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
var apiRtt = new List<double>();
var swRtt = new List<double>();
int lost = 0;
{
    using var ping = new Ping();
    var buf = new byte[32];
    var opts = new PingOptions(64, true);
    for (int i = 0; i < WarmUp + N; i++)
    {
        var sw = Stopwatch.StartNew();
        PingReply r;
        try { r = ping.Send(gwIp, 1000, buf, opts); }
        catch (PingException e) { Console.WriteLine($"ОШИБКА: {e.Message}"); break; }
        sw.Stop();
        if (i >= WarmUp)
        {
            if (r.Status == IPStatus.Success)
            {
                apiRtt.Add(r.RoundtripTime);
                swRtt.Add(sw.Elapsed.TotalMilliseconds);
            }
            else lost++;
        }
        Thread.Sleep(20);
    }
}
if (apiRtt.Count > 0)
{
    var a = Stats(apiRtt);
    var b = Stats(swRtt);
    Console.WriteLine($"Успешно {apiRtt.Count}, потеряно {lost}");
    Console.WriteLine($"API RoundtripTime : min {F(a.min)} avg {F(a.avg)} p95 {F(a.p95)} max {F(a.max)} мс, stddev {F(a.stddev)}");
    Console.WriteLine($"  → различимых значений RTT в выборке: {apiRtt.Distinct().Count()}  ({string.Join(", ", apiRtt.Distinct().OrderBy(x => x).Take(8))} ...)");
    Console.WriteLine($"Stopwatch         : min {F(b.min)} avg {F(b.avg)} p95 {F(b.p95)} p99 {F(b.p99)} max {F(b.max)} мс, stddev {F(b.stddev)}");
    Console.WriteLine($"  → различимых значений: {swRtt.Distinct().Count()}");
    Console.WriteLine($"Jitter RFC3550 по API      : {F(Rfc3550Jitter(apiRtt))} мс");
    Console.WriteLine($"Jitter RFC3550 по Stopwatch: {F(Rfc3550Jitter(swRtt))} мс");
    Console.WriteLine($"PDV (p99-p50) по Stopwatch : {F(b.p99 - b.p50)} мс");
    Console.WriteLine($"Оверхед Stopwatch над API  : {F(b.avg - a.avg)} мс (наш замер включает user-mode путь)");
}
long gcPauseAfter = GC.GetTotalPauseDuration().Ticks;
Console.WriteLine($"GC за время замера: gen0 {GC.CollectionCount(0) - gen0}, gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}; " +
                  $"суммарная пауза {F(TimeSpan.FromTicks(gcPauseAfter - gcPauseBefore).TotalMilliseconds)} мс");

// --------------------------------------------------------------- E. Шлюз, async
H($"E. ПИНГ ШЛЮЗА {gw} — асинхронный (async/await), N={N}");
{
    var aApi = new List<double>();
    var aSw = new List<double>();
    using var ping = new Ping();
    var buf = new byte[32];
    for (int i = 0; i < WarmUp + N; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = await ping.SendPingAsync(gwIp, 1000, buf);
        sw.Stop();
        if (i >= WarmUp && r.Status == IPStatus.Success)
        {
            aApi.Add(r.RoundtripTime);
            aSw.Add(sw.Elapsed.TotalMilliseconds);
        }
        await Task.Delay(20);
    }
    if (aSw.Count > 0)
    {
        var a = Stats(aApi);
        var b = Stats(aSw);
        Console.WriteLine($"API RoundtripTime : avg {F(a.avg)} p95 {F(a.p95)} max {F(a.max)} мс");
        Console.WriteLine($"Stopwatch         : avg {F(b.avg)} p95 {F(b.p95)} p99 {F(b.p99)} max {F(b.max)} мс, stddev {F(b.stddev)}");
        Console.WriteLine($"Jitter RFC3550 (Stopwatch): {F(Rfc3550Jitter(aSw))} мс");
        Console.WriteLine($"→ Дельта async vs sync по avg: сравни с секцией D");
    }
}

// ------------------------------------------------------- F. TTL / traceroute
H("F. TTL-РЕЖИМ (traceroute) БЕЗ ADMIN — до 1.1.1.1");
{
    using var ping = new Ping();
    var buf = new byte[32];
    for (int ttl = 1; ttl <= 8; ttl++)
    {
        var opts = new PingOptions(ttl, true);
        var sw = Stopwatch.StartNew();
        PingReply r;
        try { r = ping.Send(IPAddress.Parse("1.1.1.1"), 1500, buf, opts); }
        catch (PingException e) { Console.WriteLine($"  ttl={ttl}: ОШИБКА {e.InnerException?.Message ?? e.Message}"); continue; }
        sw.Stop();
        string addr = r.Address?.ToString() ?? "-";
        Console.WriteLine($"  ttl={ttl,2}  {r.Status,-20} {addr,-16} {F(sw.Elapsed.TotalMilliseconds),8} мс (API: {r.RoundtripTime} мс)");
        if (r.Status == IPStatus.Success) break;
    }
}

// --------------------------------------------------------- G. DontFragment/PMTU
H("G. DONT-FRAGMENT / PMTU БЕЗ ADMIN — поиск MTU до шлюза");
{
    using var ping = new Ping();
    int lo = 500, hi = 9000, best = 0;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        var r = ping.Send(gwIp, 1000, new byte[mid], new PingOptions(64, true));
        if (r.Status == IPStatus.Success) { best = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    Console.WriteLine(best > 0
        ? $"Максимальный payload с DF: {best} байт → PMTU ≈ {best + 28} байт"
        : "DF-режим не дал результата (устройство не отвечает на большие пакеты)");
}

// ------------------------------------------------------------- H. Ping sweep /24
H("H. PING-SWEEP /24 — проверка NFR «≤ 5 с»");
{
    var parts = gw.Split('.');
    string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
    foreach (int par in new[] { 64, 256 })
    {
        var sw = Stopwatch.StartNew();
        var sem = new SemaphoreSlim(par);
        var alive = 0;
        var tasks = Enumerable.Range(1, 254).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync(IPAddress.Parse(prefix + i), 1000, new byte[32]);
                if (r.Status == IPStatus.Success) Interlocked.Increment(ref alive);
            }
            catch { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        sw.Stop();
        Console.WriteLine($"параллелизм {par,3}: {F(sw.Elapsed.TotalSeconds)} с, найдено живых узлов: {alive}");
    }
}

Console.WriteLine("\n=== ГОТОВО ===");
