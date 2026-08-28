// Spike-05 — рукопожатие агента, темповка и прохождение firewall.
//
// План (docs/03-development-plan.md §7) ставит этот спайк ДО итерации И-12. При провале
// предусмотрен отказ от UDP-теста в v1 и throughput только по TCP — то есть от ответов
// зависит объём итерации, а не только её содержание.
//
// Проверяются три вещи, каждая из которых может закрыть путь:
//
//   1. Рукопожатие. Агент — портативный бинарь на чужой машине. Хранилища сертификатов
//      у него нет и права администратора ему не положены. Значит сертификат он делает
//      сам в памяти, а клиент доверяет не цепочке, а конкретному отпечатку, который
//      увидел при сопряжении. Надо убедиться, что SslStream это позволяет и что
//      подменённый сертификат действительно отвергается.
//
//   2. Темповка через настоящий сокет. Спайк-01 мерил таймер в пустом цикле и дал
//      p95 0.001 мс на гибридном spin-wait. Но генератор пакетов между тактами ещё
//      и отправляет датаграмму, а SendTo занимает время и может блокировать. Мерить
//      надо связку, а не таймер.
//
//   3. Firewall. Портативный агент без прав не может добавить правило. Вопрос
//      не в том, красиво ли это, а в том, что увидит оператор: молчаливый обрыв
//      или внятную ошибку.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

static void Header(string text)
{
    Console.WriteLine();
    Console.WriteLine($"=== {text} ===");
}

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("Spike-05 — агент: рукопожатие, темповка, firewall");
Console.WriteLine($"Права администратора: {(IsElevated() ? "ЕСТЬ" : "нет — как у портативного агента")}");

// ============================================================ 1. Рукопожатие

Header("1. Сертификат в памяти, без хранилища и без прав");

var agentCertificate = CreateSelfSigned("storm-agent");
var impostorCertificate = CreateSelfSigned("storm-agent");

Console.WriteLine($"Субъект            : {agentCertificate.Subject}");
Console.WriteLine($"Отпечаток          : {Thumbprint(agentCertificate)}");
Console.WriteLine($"Отпечаток подделки : {Thumbprint(impostorCertificate)}");
Console.WriteLine($"Годен до           : {agentCertificate.NotAfter:yyyy-MM-dd}");
Console.WriteLine($"Закрытый ключ      : {(agentCertificate.HasPrivateKey ? "есть" : "НЕТ")}");

// На Windows SslStream требует сертификат с ключом, доступным через PFX-круг:
// сертификат, собранный CreateSelfSigned, напрямую сервером не принимается.
var serverCertificate = ExportImport(agentCertificate);
var impostorServer = ExportImport(impostorCertificate);

Header("2. Первое сопряжение: клиент запоминает отпечаток");

var expected = Thumbprint(agentCertificate);
var (paired, pairingMs) = await TryHandshakeAsync(serverCertificate, expected).ConfigureAwait(false);

Console.WriteLine($"Рукопожатие        : {(paired ? "прошло" : "ОТКАЗ")}");
Console.WriteLine($"Время              : {F(pairingMs)} мс");

Header("3. Повторное подключение: подтверждение не запрашивается");

var (again, againMs) = await TryHandshakeAsync(serverCertificate, expected).ConfigureAwait(false);

Console.WriteLine($"Рукопожатие        : {(again ? "прошло по запомненному отпечатку" : "ОТКАЗ")}");
Console.WriteLine($"Время              : {F(againMs)} мс");

Header("4. Подмена сертификата: тот же субъект, другой ключ");

var (impostorAccepted, _) = await TryHandshakeAsync(impostorServer, expected).ConfigureAwait(false);

Console.WriteLine($"Подделка принята   : {(impostorAccepted ? "ДА — ЭТО ПРОВАЛ" : "нет, отвергнута")}");
Console.WriteLine("Субъект и срок у подделки те же — отличается только ключ.");
Console.WriteLine("Проверка по цепочке доверия здесь не сработала бы: самоподписанные оба.");

Header("5. Доверие по цепочке: что было бы без пиннинга");

var (chainAccepted, chainError) = await TryHandshakeWithChainValidationAsync(serverCertificate)
    .ConfigureAwait(false);

Console.WriteLine($"Принят по цепочке  : {(chainAccepted ? "да" : "нет")}");
Console.WriteLine($"Причина            : {chainError}");
Console.WriteLine("Отсюда следует: у портативного агента другого пути, кроме пиннинга, нет.");

// ============================================================ 2. Темповка

Header("6. Темповка через настоящий сокет UDP");

Console.WriteLine("Спайк-01 мерил таймер в пустом цикле. Здесь между тактами ещё");
Console.WriteLine("и отправляется датаграмма — мерится связка, а не таймер.");
Console.WriteLine();

MeasurePacing(intervalMs: 1.0, packets: 2000, payloadBytes: 172);
MeasurePacing(intervalMs: 0.5, packets: 2000, payloadBytes: 172);
MeasurePacing(intervalMs: 0.1, packets: 5000, payloadBytes: 172);

Header("7. Сколько стоит темповка в процессорном времени");

MeasureCpuCost(intervalMs: 1.0, packets: 3000);

Header("8. Предельная скорость без темповки — потолок отправки");

MeasureCeiling(packets: 20_000, payloadBytes: 1400);

// ============================================================ 3. Firewall

Header("9. Firewall: что может портативный агент без прав");

ReportFirewall();

Header("Итог");

Console.WriteLine(paired && again && !impostorAccepted
    ? "Рукопожатие: пиннинг работает, подделка отвергается, повтор не требует подтверждения."
    : "Рукопожатие: ПРОВАЛ — см. выше.");

Console.WriteLine("Темповка и firewall — числа выше, выводы в отчёте спайка.");

// ------------------------------------------------------------------ рукопожатие

static X509Certificate2 CreateSelfSigned(string name)
{
    using var key = RSA.Create(2048);

    var request = new CertificateRequest(
        $"CN={name}",
        key,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(
        new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

    request.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

    var builder = new SubjectAlternativeNameBuilder();
    builder.AddDnsName(name);
    builder.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(builder.Build());

    return request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddYears(2));
}

/// <summary>
/// Круг через PFX. Без него сервер не находит закрытый ключ: сертификат,
/// собранный в памяти, хранит ключ иначе, чем ожидает SslStream на Windows.
/// </summary>
static X509Certificate2 ExportImport(X509Certificate2 certificate) =>
    X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "spike"), "spike");

static string Thumbprint(X509Certificate2 certificate) =>
    Convert.ToHexString(SHA256.HashData(certificate.RawData));

static async Task<(bool Ok, double Ms)> TryHandshakeAsync(X509Certificate2 server, string expectedThumbprint)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();

    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverTask = ServeAsync(listener, server);

    var watch = Stopwatch.StartNew();

    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

        using var ssl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                    expectedThumbprint,
                    StringComparison.Ordinal));

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "storm-agent",
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }).ConfigureAwait(false);

        watch.Stop();

        var reply = new byte[64];
        var read = await ssl.ReadAsync(reply).ConfigureAwait(false);

        return (Encoding.UTF8.GetString(reply, 0, read) == "storm-agent", watch.Elapsed.TotalMilliseconds);
    }
    catch (AuthenticationException)
    {
        return (false, watch.Elapsed.TotalMilliseconds);
    }
    catch (IOException)
    {
        return (false, watch.Elapsed.TotalMilliseconds);
    }
    finally
    {
        listener.Stop();
        await serverTask.ConfigureAwait(false);
    }
}

static async Task<(bool Ok, string Error)> TryHandshakeWithChainValidationAsync(X509Certificate2 server)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();

    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverTask = ServeAsync(listener, server);

    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

        var errors = SslPolicyErrors.None;

        using var ssl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, _, _, policyErrors) =>
            {
                errors = policyErrors;
                return policyErrors == SslPolicyErrors.None;
            });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "storm-agent",
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }).ConfigureAwait(false);

        return (true, "принят");
    }
    catch (AuthenticationException ex)
    {
        return (false, ex.InnerException?.Message ?? ex.Message);
    }
    finally
    {
        listener.Stop();
        await serverTask.ConfigureAwait(false);
    }
}

static async Task ServeAsync(TcpListener listener, X509Certificate2 certificate)
{
    try
    {
        using var connection = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        using var ssl = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);

        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }).ConfigureAwait(false);

        await ssl.WriteAsync(Encoding.UTF8.GetBytes("storm-agent")).ConfigureAwait(false);
        await ssl.FlushAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
        // Клиент мог оборвать рукопожатие — это и есть проверяемое поведение.
    }
}

// ------------------------------------------------------------------ темповка

/// <summary>
/// Гибридный spin-wait: отдаём квант, пока до цели далеко, дальше крутимся.
/// Sleep(1) спит вдвое дольше запрошенного (спайк-01), поэтому его здесь нет.
/// </summary>
static void WaitUntil(long targetTicks)
{
    var spin = new SpinWait();

    while (true)
    {
        var left = targetTicks - Stopwatch.GetTimestamp();

        if (left <= 0)
        {
            return;
        }

        if (left > Stopwatch.Frequency / 2000)
        {
            Thread.Sleep(0);
            continue;
        }

        spin.SpinOnce(-1);
    }
}

static void MeasurePacing(double intervalMs, int packets, int payloadBytes)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    sink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var target = (IPEndPoint)sink.LocalEndPoint!;

    // Приёмник не читает: очередь заполнится и датаграммы начнут отбрасываться ядром.
    // Нас интересует стоимость отправки, а не доставки.
    var payload = new byte[payloadBytes];
    var errors = new double[packets];

    var ticksPerInterval = (long)(Stopwatch.Frequency * intervalMs / 1000.0);
    var thread = new Thread(() =>
    {
        var next = Stopwatch.GetTimestamp() + ticksPerInterval;

        for (var i = 0; i < packets; i++)
        {
            WaitUntil(next);

            var actual = Stopwatch.GetTimestamp();
            errors[i] = (actual - next) * 1000.0 / Stopwatch.Frequency;

            try
            {
                socket.SendTo(payload, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // Переполненная очередь — ожидаемо, темповку это не измеряет.
            }

            next += ticksPerInterval;
        }
    })
    {
        IsBackground = true,
        Priority = ThreadPriority.Highest,
    };

    var watch = Stopwatch.StartNew();
    thread.Start();
    thread.Join();
    watch.Stop();

    Array.Sort(errors);

    var expected = packets * intervalMs;
    var achieved = packets / watch.Elapsed.TotalSeconds;

    Console.WriteLine($"Интервал {F(intervalMs)} мс, пакетов {packets}:");
    Console.WriteLine($"  ошибка такта p50 {F(errors[packets / 2])}  p95 {F(errors[(int)(packets * 0.95)])}  "
                      + $"p99 {F(errors[(int)(packets * 0.99)])}  max {F(errors[^1])} мс");
    Console.WriteLine($"  задумано {F(expected)} мс, вышло {F(watch.Elapsed.TotalMilliseconds)} мс, "
                      + $"{achieved.ToString("0", CultureInfo.InvariantCulture)} пакетов/с");
}

static void MeasureCpuCost(double intervalMs, int packets)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    sink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var target = (IPEndPoint)sink.LocalEndPoint!;
    var payload = new byte[172];

    var before = Process.GetCurrentProcess().TotalProcessorTime;
    var watch = Stopwatch.StartNew();

    var ticksPerInterval = (long)(Stopwatch.Frequency * intervalMs / 1000.0);
    var thread = new Thread(() =>
    {
        var next = Stopwatch.GetTimestamp() + ticksPerInterval;

        for (var i = 0; i < packets; i++)
        {
            WaitUntil(next);

            try
            {
                socket.SendTo(payload, SocketFlags.None, target);
            }
            catch (SocketException)
            {
            }

            next += ticksPerInterval;
        }
    })
    {
        IsBackground = true,
        Priority = ThreadPriority.Highest,
    };

    thread.Start();
    thread.Join();
    watch.Stop();

    var cpu = (Process.GetCurrentProcess().TotalProcessorTime - before).TotalMilliseconds;
    var share = cpu / watch.Elapsed.TotalMilliseconds * 100;

    Console.WriteLine($"За {F(watch.Elapsed.TotalMilliseconds)} мс потрачено {F(cpu)} мс процессорного времени.");
    Console.WriteLine($"Это {share.ToString("0", CultureInfo.InvariantCulture)} % одного ядра "
                      + $"из {Environment.ProcessorCount} — цена точной темповки.");
}

static void MeasureCeiling(int packets, int payloadBytes)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    sink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var target = (IPEndPoint)sink.LocalEndPoint!;
    var payload = new byte[payloadBytes];

    var watch = Stopwatch.StartNew();

    for (var i = 0; i < packets; i++)
    {
        try
        {
            socket.SendTo(payload, SocketFlags.None, target);
        }
        catch (SocketException)
        {
        }
    }

    watch.Stop();

    var rate = packets / watch.Elapsed.TotalSeconds;
    var mbps = rate * payloadBytes * 8 / 1_000_000.0;

    Console.WriteLine($"Без темповки: {rate.ToString("0", CultureInfo.InvariantCulture)} пакетов/с "
                      + $"по {payloadBytes} байт — {mbps.ToString("0", CultureInfo.InvariantCulture)} Мбит/с.");
    Console.WriteLine("Это потолок отправки на этой машине, а не пропускная способность сети.");
}

// ------------------------------------------------------------------ firewall

static void ReportFirewall()
{
    Console.WriteLine("Слушающий сокет открывается без прав — проверяем:");

    try
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"  bind + listen на 0.0.0.0:{port} — успешно, прав не потребовалось.");
        listener.Stop();
    }
    catch (SocketException ex)
    {
        Console.WriteLine($"  bind + listen — ОТКАЗ: {ex.SocketErrorCode}");
    }

    Console.WriteLine();
    Console.WriteLine("Состояние профилей Windows Defender Firewall:");
    Run("netsh", "advfirewall show allprofiles state");

    // Главное число раздела. Если политика для входящих — блокировать, то слушающий
    // сокет откроется, но снаружи до него никто не достучится: агент будет молча
    // не отвечать, а оператор увидит таймаут вместо причины.
    Console.WriteLine("Политика по умолчанию (входящие/исходящие):");
    Run("netsh", "advfirewall show allprofiles firewallpolicy");

    Console.WriteLine("Попытка добавить правило без прав администратора:");
    Run("netsh", "advfirewall firewall add rule name=\"storm-spike-05\" dir=in action=allow protocol=TCP localport=51999");

    Console.WriteLine("Уборка (если правило всё же появилось):");
    Run("netsh", "advfirewall firewall delete rule name=\"storm-spike-05\"");
}

static void Run(string file, string arguments)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            Console.WriteLine("  запустить не удалось");
            return;
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);

        foreach (var line in output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
        {
            Console.WriteLine($"  {line}");
        }

        Console.WriteLine($"  (код возврата {process.ExitCode})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    }
}

static bool IsElevated()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
