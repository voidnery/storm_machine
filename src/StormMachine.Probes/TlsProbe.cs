using System.Globalization;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes;

/// <summary>
/// Проба TLS: рукопожатие, сертификат, версия протокола и набор шифров.
/// </summary>
/// <remarks>
/// Форма результата почти целиком фактическая: чисел здесь три (фазы рукопожатия),
/// а всё остальное — кто выдал сертификат, до какого числа он годен, каким протоколом
/// и шифром договорились. Ряда не образует, перцентили считать не по чему.
/// <para>
/// Самая частая авария, которую ловит эта проба, — истёкший сертификат. Она предсказуема
/// и потому обиднее прочих: срок известен заранее, о нём просто некому было напомнить.
/// </para>
/// </remarks>
public sealed class TlsProbe(IHighResolutionClock clock) : IProbe
{
    /// <summary>За сколько дней до истечения сертификат считается проблемой.</summary>
    private const int ExpiryWarningDays = 30;

    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Tls,
        Shape = ProbeResultShape.PhasedTiming,
        Name = "tls",
        Title = "TLS-инспектор",
        Description = "Рукопожатие, цепочка сертификатов, срок действия, версия протокола и шифр.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.TlsHandshake,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "port", Label = "Порт", Type = ProbeParameterType.Integer,
                DefaultValue = 443, Minimum = 1, Maximum = 65535,
                Description = "Порт назначения.",
            },
            new ProbeParameter
            {
                Name = "count", Label = "Число рукопожатий", Type = ProbeParameterType.Integer,
                DefaultValue = 1, Minimum = 1, Maximum = 1000,
                Description = "Сколько раз выполнить рукопожатие.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 10_000, Minimum = 1, Maximum = 60_000,
                Description = "Общий предел на установление соединения.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request) =>
        ProbeValidation.Validate(Descriptor, request);

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var port = request.GetParameter("port", 443);
        var count = request.GetParameter("count", 1);
        var timeoutMs = request.GetParameter("timeout", 10_000);

        var host = ExtractHost(request.Target, ref port);
        observer.OnResolved($"{host}:{port}");

        for (var attempt = 0; attempt < count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timestampUtc = DateTimeOffset.UtcNow;
            var phaseSamples = new List<Sample>(3);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            TimedConnectionResult? connection = null;
            SampleStatus failure = SampleStatus.Success;

            try
            {
                connection = await TimedConnection
                    .OpenAsync(_clock, host, port, useTls: true, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failure = SampleStatus.Timeout;
            }
            catch (AuthenticationException ex)
            {
                failure = SampleStatus.Rejected;
                observer.OnFact(ProbeFact.Warning("tls", "Рукопожатие", ex.Message));
            }
            catch (Exception ex)
            {
                failure = SampleStatus.Error;
                observer.OnFact(ProbeFact.Warning("tls", "Ошибка", ex.Message));
            }

            if (connection is null)
            {
                yield return Sample.Failed(attempt, timestampUtc, failure) with { Label = "tls", Group = attempt };
                continue;
            }

            await using (connection.ConfigureAwait(false))
            {
                phaseSamples.Add(Phase(attempt, timestampUtc, "dns", connection.Phases.DnsMs));
                phaseSamples.Add(Phase(attempt, timestampUtc, "connect", connection.Phases.ConnectMs));
                phaseSamples.Add(Phase(attempt, timestampUtc, "tls", connection.Phases.TlsMs));

                if (attempt == 0)
                {
                    ReportCertificate(observer, connection, host);
                }
            }

            foreach (var sample in phaseSamples)
            {
                yield return sample;
            }
        }
    }

    private static Sample Phase(int attempt, DateTimeOffset timestampUtc, string label, double value) => new()
    {
        Sequence = attempt,
        TimestampUtc = timestampUtc,
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = attempt,
    };

    private static void ReportCertificate(IProbeObserver observer, TimedConnectionResult connection, string host)
    {
        var ssl = connection.Ssl;

        if (ssl is not null)
        {
            observer.OnFact(ProbeFact.Text("tls", "Протокол", ssl.SslProtocol.ToString()));
            observer.OnFact(ProbeFact.Text("tls", "Шифр", $"{ssl.NegotiatedCipherSuite}"));

            // Анализатор помечает SslProtocols.Tls и Tls11 как устаревшие и требует их
            // не использовать. Здесь мы их не выбираем, а РАСПОЗНАЁМ: инструмент диагностики
            // обязан уметь назвать устаревший протокол, если сервер согласился именно на него.
            // Запретить себе произносить имя проблемы — не то же самое, что решить проблему.
#pragma warning disable SYSLIB0039, CA5397
            var isObsoleteProtocol = ssl.SslProtocol is SslProtocols.Tls or SslProtocols.Tls11;
#pragma warning restore SYSLIB0039, CA5397

            if (isObsoleteProtocol)
            {
                observer.OnFact(ProbeFact.Warning("tls", "Устаревший протокол",
                    $"{ssl.SslProtocol} — версии ниже TLS 1.2 считаются небезопасными."));
            }
        }

        var certificate = connection.RemoteCertificate;
        if (certificate is null)
        {
            observer.OnFact(ProbeFact.Warning("tls", "Сертификат", "сервер не предъявил сертификат"));
            return;
        }

        observer.OnFact(ProbeFact.Text("tls", "Субъект", certificate.Subject));
        observer.OnFact(ProbeFact.Text("tls", "Издатель", certificate.Issuer));
        observer.OnFact(ProbeFact.Text("tls", "Годен с", certificate.NotBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        var daysLeft = (certificate.NotAfter - DateTime.Now).TotalDays;
        var validUntil = certificate.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        observer.OnFact(daysLeft switch
        {
            < 0 => ProbeFact.Warning("tls", "Годен до", $"{validUntil} — ИСТЁК {Math.Abs(daysLeft):0} дней назад"),
            < ExpiryWarningDays => ProbeFact.Warning("tls", "Годен до", $"{validUntil} — осталось {daysLeft:0} дней"),
            _ => ProbeFact.Text("tls", "Годен до", $"{validUntil} — осталось {daysLeft:0} дней"),
        });

        // Число днями отдельным фактом, и в любом состоянии сертификата.
        // Раньше оно попадало в результат только когда с сертификатом всё хорошо:
        // у истекающего Numeric оставался пустым — то есть ровно тогда, когда порог
        // «осталось меньше двух недель» и должен был сработать, срабатывать было нечему.
        // Это же причина, по которой факт для чтения и факт для порога разделены:
        // текст «2026-10-28 — осталось 62 дней» человеку понятнее, а сравнивать
        // с порогом нужно число.
        observer.OnFact(new ProbeFact
        {
            Category = "tls",
            Name = "Осталось дней",
            Value = daysLeft.ToString("0", CultureInfo.InvariantCulture),
            Numeric = Math.Floor(daysLeft),
            Unit = MeasurementUnit.Count,
            IsWarning = daysLeft < ExpiryWarningDays,
        });

        observer.OnFact(ProbeFact.Text("tls", "Алгоритм ключа", certificate.PublicKey.Oid.FriendlyName ?? "неизвестен"));

        var san = certificate.Extensions
            .FirstOrDefault(e => e.Oid?.Value == "2.5.29.17")?
            .Format(false);

        if (!string.IsNullOrWhiteSpace(san))
        {
            observer.OnFact(ProbeFact.Text("tls", "Альтернативные имена", Shorten(san, 300)));
        }

        if (connection.Chain is { } chain && chain.ChainElements.Count > 0)
        {
            observer.OnFact(ProbeFact.Text("tls", "Длина цепочки", $"{chain.ChainElements.Count}"));
        }

        ReportPolicyErrors(observer, connection.PolicyErrors, host);
    }

    private static void ReportPolicyErrors(IProbeObserver observer, SslPolicyErrors errors, string host)
    {
        if (errors == SslPolicyErrors.None)
        {
            observer.OnFact(ProbeFact.Text("tls", "Проверка", "цепочка и имя подтверждены"));
            return;
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            observer.OnFact(ProbeFact.Warning("tls", "Имя", $"сертификат не выписан на «{host}»"));
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            observer.OnFact(ProbeFact.Warning("tls", "Цепочка", "не выстраивается до доверенного корня"));
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            observer.OnFact(ProbeFact.Warning("tls", "Сертификат", "не предъявлен"));
        }
    }

    private static string ExtractHost(Target target, ref int port)
    {
        if (target.Kind != TargetKind.Url)
        {
            return target.Value;
        }

        var uri = new Uri(target.Value);
        if (!uri.IsDefaultPort)
        {
            port = uri.Port;
        }

        return uri.Host;
    }

    private static string Shorten(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";
}
