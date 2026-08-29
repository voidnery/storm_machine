using System.Collections;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Alerting.UnitTests;

/// <summary>
/// Настройки в памяти.
/// </summary>
/// <remarks>
/// Настоящее хранилище проверяется своими тестами; здесь предмет проверки — каналы,
/// и подмена базы словарём убирает из теста всё, кроме них.
/// </remarks>
internal sealed class FakeSettings : ISettingsStore, IEnumerable<KeyValuePair<string, string?>>
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set => _values[key] = value;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(
        string key,
        string? value,
        bool secret = false,
        CancellationToken cancellationToken = default)
    {
        _values[key] = value;

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.Remove(key));

    public Task<IReadOnlyList<SettingEntry>> ListAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettingEntry>>(
        [
            .. _values
                .Where(p => prefix is null || p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => new SettingEntry(p.Key, p.Value, IsSecret: false)),
        ]);

    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Готовое оповещение для проверок, которым важен не его состав.</summary>
internal static class AlertFixture
{
    public static AlertNotification Notification()
    {
        var monitorId = Guid.NewGuid();

        var monitor = new Monitor
        {
            Id = monitorId,
            Name = "Шлюз",
            Target = Target.Ip("192.168.1.1"),
            Subject = "ping",
            Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
        };

        var check = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitorId,
            StartedUtc = DateTimeOffset.UnixEpoch,
            Level = VerdictLevel.Fail,
            Summary = "цель не отвечает",
        };

        var alert = new AlertEvent
        {
            Id = Guid.NewGuid(),
            MonitorId = monitorId,
            MonitorName = "Шлюз",
            AtUtc = DateTimeOffset.UnixEpoch,
            Action = AlertAction.Raised,
            Level = VerdictLevel.Fail,
            Reason = "три отказа подряд",
        };

        return new AlertNotification(monitor, alert, check);
    }
}
