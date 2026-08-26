using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes;

/// <summary>
/// Проба DNS: задержка резолверов и сравнение их ответов между собой.
/// </summary>
/// <remarks>
/// Единственная проба, где цель — это <b>имя для разрешения</b>, а не адрес назначения.
/// Разрешать её заранее нельзя: разрешение и есть предмет измерения.
/// <para>
/// Форма результата принципиально отличается от ICMP: измерений столько, сколько
/// резолверов, они не образуют один ряд, а сравниваются между собой. Плюс каждый ответ
/// несёт записи — факты, а не числа. Именно на этой пробе стало видно, что скалярной
/// модели сэмпла недостаточно.
/// </para>
/// <para>
/// Расхождение ответов между резолверами — не курьёз, а рабочий диагноз: так выглядят
/// подмена DNS, региональная балансировка CDN и незавершённая миграция записей.
/// </para>
/// </remarks>
public sealed class DnsProbe(IHighResolutionClock clock, INetworkEnvironment environment) : IProbe
{
    private static readonly string[] WellKnownResolvers = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Dns,
        Shape = ProbeResultShape.ComparedSeries,
        Name = "dns",
        Title = "DNS-инспектор",
        Description = "Задержка резолверов, полученные записи и расхождения между резолверами.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.DnsQuery,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "type", Label = "Тип записи", Type = ProbeParameterType.Text,
                DefaultValue = "A",
                Description = "A, AAAA, NS, CNAME, MX, TXT, PTR, SOA.",
            },
            new ProbeParameter
            {
                Name = "resolvers", Label = "Резолверы", Type = ProbeParameterType.Text,
                DefaultValue = "",
                Description = "Список через запятую. Пусто — системные плюс публичные.",
            },
            new ProbeParameter
            {
                Name = "count", Label = "Запросов на резолвер", Type = ProbeParameterType.Integer,
                DefaultValue = 3, Minimum = 1, Maximum = 1000,
                Description = "Сколько раз опросить каждый резолвер.",
            },
            new ProbeParameter
            {
                Name = "timeout", Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 3000, Minimum = 1, Maximum = 60_000,
                Description = "Сколько ждать ответа.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>(ProbeValidation.Validate(Descriptor, request));

        var type = request.GetParameter("type", "A");
        try
        {
            DnsWire.ParseRecordType(type);
        }
        catch (ArgumentException)
        {
            errors.Add(new ProbeValidationError("type", $"Неизвестный тип записи «{type}»."));
        }

        if (request.Target.Kind is TargetKind.Subnet or TargetKind.DefaultGateway)
        {
            errors.Add(new ProbeValidationError("target", "Цель DNS-пробы — имя для разрешения, а не адрес назначения."));
        }

        return errors;
    }

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var recordTypeName = request.GetParameter("type", "A").ToUpperInvariant();
        var recordType = DnsWire.ParseRecordType(recordTypeName);
        var count = request.GetParameter("count", 3);
        var timeoutMs = request.GetParameter("timeout", 3000);

        var queryName = request.Target.Kind == TargetKind.Url
            ? new Uri(request.Target.Value).Host
            : request.Target.Value;

        var resolvers = ResolveResolverList(request.GetParameter("resolvers", string.Empty));

        observer.OnResolved(queryName);
        observer.OnFact(ProbeFact.Text("dns", "Запрос", $"{queryName} {recordTypeName}"));

        var answersByResolver = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sequence = 0;

        for (var resolverIndex = 0; resolverIndex < resolvers.Count; resolverIndex++)
        {
            var resolver = resolvers[resolverIndex];

            for (var attempt = 0; attempt < count; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startedAt = _clock.GetTimestamp();
                var timestampUtc = DateTimeOffset.UtcNow;

                var (sample, response) = await QueryOnceAsync(
                    resolver, queryName, recordType, sequence, resolverIndex, timestampUtc, startedAt, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                // Записи собираются только с первой удачной попытки: повторы измеряют
                // задержку, а не содержимое, и дублировать одни и те же ответы незачем.
                if (response is not null && attempt == 0)
                {
                    ReportAnswers(observer, resolver, response, answersByResolver);
                }

                sequence++;
                yield return sample;
            }
        }

        ReportConsistency(observer, answersByResolver);
    }

    private static void ReportAnswers(
        IProbeObserver observer,
        string resolver,
        DnsResponse response,
        Dictionary<string, List<string>> answersByResolver)
    {
        if (!response.IsSuccess)
        {
            observer.OnFact(ProbeFact.Warning("dns", resolver, response.ResponseCodeName));
            answersByResolver[resolver] = [response.ResponseCodeName];
            return;
        }

        if (response.Answers.Count == 0)
        {
            observer.OnFact(ProbeFact.Text("dns", resolver, "NOERROR, но записей нет"));
            answersByResolver[resolver] = [];
            return;
        }

        var values = new List<string>();

        foreach (var record in response.Answers)
        {
            values.Add($"{record.Type} {record.Value}");
            observer.OnFact(new ProbeFact
            {
                Category = "dns",
                Name = $"{resolver} → {record.Type}",
                Value = record.Value,
                Numeric = record.Ttl,
                Unit = MeasurementUnit.Count,
            });
        }

        answersByResolver[resolver] = values;

        if (response.IsTruncated)
        {
            observer.OnFact(ProbeFact.Warning("dns", resolver, "Ответ усечён — потребовался бы TCP."));
        }
    }

    private static void ReportConsistency(IProbeObserver observer, Dictionary<string, List<string>> answersByResolver)
    {
        if (answersByResolver.Count < 2)
        {
            return;
        }

        var signatures = answersByResolver.ToDictionary(
            pair => pair.Key,
            pair => string.Join(" | ", pair.Value.OrderBy(v => v, StringComparer.Ordinal)),
            StringComparer.Ordinal);

        var distinct = signatures.Values.Distinct(StringComparer.Ordinal).Count();

        if (distinct == 1)
        {
            observer.OnFact(ProbeFact.Text("dns", "Согласованность", "все резолверы вернули одно и то же"));
            return;
        }

        observer.OnFact(ProbeFact.Warning(
            "dns",
            "Согласованность",
            $"резолверы разошлись: {distinct} различных ответа. "
            + "Так выглядят подмена DNS, региональная балансировка CDN или незавершённая миграция записей."));

        foreach (var (resolver, signature) in signatures)
        {
            observer.OnFact(ProbeFact.Text("dns", $"Вариант {resolver}", signature.Length == 0 ? "(пусто)" : signature));
        }
    }

    private async Task<(Sample Sample, DnsResponse? Response)> QueryOnceAsync(
        string resolver,
        string queryName,
        ushort recordType,
        int sequence,
        int group,
        DateTimeOffset timestampUtc,
        long startedAt,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(resolver, out var resolverAddress))
        {
            return (Sample.Failed(sequence, timestampUtc, SampleStatus.Error) with { Label = resolver, Group = group }, null);
        }

        var endpoint = new IPEndPoint(resolverAddress, 53);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = DnsWire.BuildQuery(id, queryName, recordType);
        var buffer = new byte[4096];

        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await socket.SendToAsync(query, SocketFlags.None, endpoint, timeoutCts.Token).ConfigureAwait(false);

            var received = await socket
                .ReceiveFromAsync(buffer, SocketFlags.None, endpoint, timeoutCts.Token)
                .ConfigureAwait(false);

            var elapsed = _clock.ElapsedMilliseconds(startedAt);
            var response = DnsWire.Parse(buffer.AsSpan(0, received.ReceivedBytes));

            var sample = new Sample
            {
                Sequence = sequence,
                TimestampUtc = timestampUtc,
                Value = elapsed,
                Status = response.IsSuccess ? SampleStatus.Success : SampleStatus.Rejected,
                Label = resolver,
                Group = group,
                RespondedBy = response.IsSuccess ? null : response.ResponseCodeName,
            };

            return (sample, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (Sample.Failed(sequence, timestampUtc, SampleStatus.Timeout) with { Label = resolver, Group = group }, null);
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            return (Sample.Failed(sequence, timestampUtc, SampleStatus.Error) with { Label = resolver, Group = group }, null);
        }
    }

    /// <summary>
    /// Составляет список резолверов: сначала системные, затем публичные для сравнения.
    /// </summary>
    private List<string> ResolveResolverList(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return [.. configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        var list = new List<string>();

        var adapter = _environment.GetPrimaryAdapter();
        if (adapter is not null)
        {
            foreach (var server in adapter.DnsServers)
            {
                if (!list.Contains(server, StringComparer.Ordinal))
                {
                    list.Add(server);
                }
            }
        }

        foreach (var server in WellKnownResolvers)
        {
            if (!list.Contains(server, StringComparer.Ordinal))
            {
                list.Add(server);
            }
        }

        return list;
    }
}
