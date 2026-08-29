using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Хранилище мониторов в памяти.
/// </summary>
/// <remarks>
/// Настоящее хранилище проверяется своими тестами; здесь предмет проверки — планировщик,
/// и подмена базы словарём убирает из теста всё, кроме него.
/// </remarks>
internal sealed class FakeMonitorStore : IMonitorStore
{
    private readonly Dictionary<Guid, Monitor> _monitors = [];
    private readonly Dictionary<Guid, MonitorStatus> _statuses = [];

    public List<MonitorCheck> Checks { get; } = [];

    public List<AlertEvent> Alerts { get; } = [];

    public Task<IReadOnlyList<Monitor>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Monitor>>([.. _monitors.Values]);

    public Task<Monitor?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_monitors.GetValueOrDefault(id));

    public Task<Monitor?> FindAsync(string nameOrId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_monitors.Values.FirstOrDefault(m =>
            string.Equals(m.Name, nameOrId, StringComparison.OrdinalIgnoreCase)));

    public Task SaveAsync(Monitor monitor, CancellationToken cancellationToken = default)
    {
        _monitors[monitor.Id] = monitor;

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_monitors.Remove(id));

    public Task SetNextDueAsync(Guid id, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken = default)
    {
        if (_monitors.TryGetValue(id, out var monitor))
        {
            _monitors[id] = monitor with { NextDueUtc = nextDueUtc };
        }

        return Task.CompletedTask;
    }

    public Task<MonitorStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_statuses.GetValueOrDefault(id, MonitorStatus.Fresh));

    public Task SaveStatusAsync(Guid id, MonitorStatus status, CancellationToken cancellationToken = default)
    {
        _statuses[id] = status;

        return Task.CompletedTask;
    }

    public Task AppendCheckAsync(MonitorCheck check, CancellationToken cancellationToken = default)
    {
        lock (Checks)
        {
            Checks.Add(check);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MonitorCheck>> ListChecksAsync(
        CheckQuery query,
        CancellationToken cancellationToken = default)
    {
        lock (Checks)
        {
            return Task.FromResult<IReadOnlyList<MonitorCheck>>(
                [.. Checks.Where(c => query.MonitorId is null || c.MonitorId == query.MonitorId)]);
        }
    }

    public Task AppendAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default)
    {
        lock (Alerts)
        {
            Alerts.Add(alert);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlertEvent>> ListAlertsAsync(
        AlertQuery query,
        CancellationToken cancellationToken = default)
    {
        lock (Alerts)
        {
            return Task.FromResult<IReadOnlyList<AlertEvent>>([.. Alerts]);
        }
    }
}

/// <summary>Проба, отдающая заданное значение столько раз, сколько попросят.</summary>
internal sealed class FakeProbe(Func<double> value) : IProbe
{
    public int Runs { get; private set; }

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Icmp,
        Shape = ProbeResultShape.ScalarSeries,
        Name = "fake",
        Title = "Проба для теста",
        Description = "Отдаёт заданное значение.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.IcmpEcho,
        Parameters = [],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request) => [];

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Runs++;

        for (var i = 0; i < 4; i++)
        {
            yield return new Sample
            {
                Sequence = i,
                TimestampUtc = DateTimeOffset.UtcNow,
                Value = value(),
                Status = SampleStatus.Success,
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal sealed class FakeRegistry(IProbe probe) : IProbeRegistry
{
    public IReadOnlyList<ProbeDescriptor> Descriptors => [probe.Descriptor];

    public bool TryGet(string name, out IProbe found)
    {
        found = probe;

        return true;
    }
}

/// <summary>Журнал прогонов, который ничего не пишет: предмет проверки не он.</summary>
internal sealed class NullRunStore : IRunStore
{
    public string Location => "в памяти";

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IRunWriter> BeginRunAsync(RunDescriptor descriptor, CancellationToken cancellationToken = default) =>
        Task.FromResult<IRunWriter>(new NullRunWriter());

    public Task<IReadOnlyList<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RunSummary>>([]);

    public Task<StoredRun?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoredRun?>(null);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<RetentionReport> ApplyRetentionAsync(
        RetentionPolicy policy,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<(long SizeBytes, int RunCount, long SampleCount)> GetUsageAsync(
        CancellationToken cancellationToken = default) => Task.FromResult((0L, 0, 0L));
}

/// <summary>Запись прогона, которая ничего не пишет, но выдаёт идентификатор.</summary>
internal sealed class NullRunWriter : IRunWriter
{
    public Guid RunId { get; } = Guid.NewGuid();

    public ValueTask AppendAsync(Sample sample, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public Task CompleteAsync(
        IReadOnlyList<ProbeFact> facts,
        string? resolvedAddress,
        bool wasCancelled,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NullClock : IHighResolutionClock
{
    public double ResolutionNanoseconds => 100;

    public double CalibrationBaselineMs => 0.1;

    public long GetTimestamp() => 0;

    public double ElapsedMilliseconds(long startTimestamp) => 0;

    public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) => 0;

    public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NullEnvironment : INetworkEnvironment
{
    public bool IsElevated => false;

    public NetworkAdapter? GetPrimaryAdapter() => null;

    public IReadOnlyList<NetworkAdapter> GetAdapters() => [];
}

/// <summary>Канал, который только считает, что ему передали.</summary>
internal sealed class RecordingChannel : IAlertChannel
{
    public List<AlertNotification> Sent { get; } = [];

    public string Name => "тест";

    public string Title => "Канал для теста";

    public bool IsConfigured => true;

    public string? MissingConfiguration => null;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(notification);
        }

        return Task.CompletedTask;
    }
}

internal static class Fakes
{
    public static Monitor Monitor(Schedule schedule, string name = "тест") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Subject = "fake",
        Target = Target.Host("example.test"),
        Schedule = schedule,
        Thresholds = [Domain.Scenarios.Threshold.Parse("p95 < 100")],
    };
}

/// <summary>
/// Хранилище профилей, которое не читается.
/// </summary>
/// <remarks>
/// Проверяет, что сбой необязательной части не срывает измерение: профили —
/// удобство, а не условие работы продукта.
/// </remarks>
internal sealed class BrokenProfileStore : IProfileStore
{
    public Task<IReadOnlyList<NetworkProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task<NetworkProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task<NetworkProfile?> FindAsync(string nameOrId, CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task<NetworkProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task ActivateAsync(Guid? id, CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new IOException("база профилей недоступна");
}
