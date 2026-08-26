using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using StormMachine.Application.Abstractions;

namespace StormMachine.Probes;

/// <summary>Длительности фаз установления соединения.</summary>
internal sealed record ConnectionPhases
{
    public double DnsMs { get; init; }

    public double ConnectMs { get; init; }

    public double TlsMs { get; init; }

    public IPAddress? Address { get; init; }
}

/// <summary>Результат установления соединения с разбивкой по фазам.</summary>
internal sealed class TimedConnectionResult : IAsyncDisposable
{
    public required Socket Socket { get; init; }

    public required Stream Stream { get; init; }

    public required ConnectionPhases Phases { get; init; }

    public SslStream? Ssl { get; init; }

    public X509Certificate2? RemoteCertificate { get; init; }

    public X509Chain? Chain { get; init; }

    public SslPolicyErrors PolicyErrors { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Ssl is not null)
        {
            await Ssl.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }

        RemoteCertificate?.Dispose();
        Chain?.Dispose();
        Socket.Dispose();
    }
}

/// <summary>
/// Установление соединения с раздельным замером фаз: разрешение имени, TCP, TLS.
/// </summary>
/// <remarks>
/// Написано вручную вместо использования <c>HttpClient</c> потому, что готовый клиент
/// не даёт разделить фазы: он отдаёт одно суммарное время. А ценность водопада именно
/// в разделении — «медленно» из-за DNS и «медленно» из-за TLS требуют разных действий.
/// <para>
/// Общая часть для проб TLS и HTTP: обе начинаются одинаково.
/// </para>
/// </remarks>
internal static class TimedConnection
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Не отключайте проверку сертификата",
        Justification =
            "Осознанное решение для инструмента диагностики. Проверка не отключена — её результат " +
            "перехватывается и попадает в факты результата (ошибки имени, цепочки, отсутствие " +
            "сертификата сообщаются оператору отдельными предупреждениями). Оборвать рукопожатие " +
            "означало бы оставить оператора без данных ровно в тот момент, когда он их и ищет: " +
            "чтобы диагностировать проблемный сертификат, его нужно сначала получить. " +
            "По этому каналу не передаётся ничего секретного — только запросы GET и HEAD.")]
    public static async Task<TimedConnectionResult> OpenAsync(
        IHighResolutionClock clock,
        string host,
        int port,
        bool useTls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        // --- Фаза 1: разрешение имени ---
        var dnsStart = clock.GetTimestamp();
        IPAddress address;

        if (IPAddress.TryParse(host, out var parsed))
        {
            address = parsed;
        }
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault()
                      ?? throw new InvalidOperationException($"Имя «{host}» не разрешается в адрес.");
        }

        var dnsMs = clock.ElapsedMilliseconds(dnsStart);

        // --- Фаза 2: TCP ---
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = new LingerOption(true, 0),
        };

        double connectMs;
        try
        {
            var connectStart = clock.GetTimestamp();
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            connectMs = clock.ElapsedMilliseconds(connectStart);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        Stream stream = new NetworkStream(socket, ownsSocket: false);

        if (!useTls)
        {
            return new TimedConnectionResult
            {
                Socket = socket,
                Stream = stream,
                Phases = new ConnectionPhases { DnsMs = dnsMs, ConnectMs = connectMs, Address = address },
            };
        }

        // --- Фаза 3: TLS ---
        var policyErrors = SslPolicyErrors.None;
        X509Certificate2? certificate = null;
        X509Chain? chain = null;

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, builtChain, errors) =>
        {
            // Диагностический инструмент обязан ПОКАЗАТЬ проблему сертификата,
            // а не оборвать соединение и оставить оператора без данных. Ошибки
            // запоминаются и попадают в факты; секретов мы по этому каналу не передаём.
            policyErrors = errors;

            if (cert is not null)
            {
                certificate = new X509Certificate2(cert);
            }

            if (builtChain is not null)
            {
                chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.Build(certificate!);
            }

            return true;
        });

        try
        {
            var tlsStart = clock.GetTimestamp();

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);

            var tlsMs = clock.ElapsedMilliseconds(tlsStart);

            return new TimedConnectionResult
            {
                Socket = socket,
                Stream = ssl,
                Ssl = ssl,
                RemoteCertificate = certificate,
                Chain = chain,
                PolicyErrors = policyErrors,
                Phases = new ConnectionPhases
                {
                    DnsMs = dnsMs,
                    ConnectMs = connectMs,
                    TlsMs = tlsMs,
                    Address = address,
                },
            };
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            certificate?.Dispose();
            chain?.Dispose();
            socket.Dispose();
            throw;
        }
    }
}
