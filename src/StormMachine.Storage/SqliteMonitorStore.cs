using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Storage;

/// <summary>
/// Мониторы, их проверки и лента алертов в той же базе, что журнал прогонов.
/// </summary>
/// <remarks>
/// Проверка ссылается на прогон, прогон лежит рядом — и это не удобство, а условие
/// осмысленности: «монитор упал в 3:14» без возможности открыть само измерение
/// не отличается от слуха.
/// </remarks>
public sealed class SqliteMonitorStore(SqliteRunStore runStore) : IMonitorStore
{
    private const string MonitorColumns =
        "id, name, description, kind, subject, target_kind, target_value, target_label, "
        + "parameters_json, thresholds_json, schedule_json, alert_json, objective_json, "
        + "preset_id, enabled, created_ticks, updated_ticks, next_due_ticks";

    private const string CheckColumns =
        "id, monitor_id, started_ticks, duration_ticks, kind, level, summary, "
        + "run_id, metric, value, threshold, missed_count, error";

    private const string AlertColumns =
        "id, monitor_id, monitor_name, at_ticks, action, level, reason, summary, "
        + "check_id, notified, channels_json, errors_json";

    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public async Task<IReadOnlyList<Monitor>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {MonitorColumns} FROM monitors ORDER BY name;";

        var monitors = new List<Monitor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            monitors.Add(ReadMonitor(reader));
        }

        return monitors;
    }

    public async Task<Monitor?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {MonitorColumns} FROM monitors WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMonitor(reader) : null;
    }

    /// <summary>
    /// Ищет по имени, его началу или началу идентификатора.
    /// </summary>
    /// <remarks>
    /// Неоднозначное сокращение — ошибка, а не догадка: выбрать за человека, какой
    /// из двух мониторов он имел в виду, нельзя. То же правило, что у агентов.
    /// </remarks>
    public async Task<Monitor?> FindAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        var needle = nameOrId.Trim();
        var monitors = await ListAsync(cancellationToken).ConfigureAwait(false);

        var exact = monitors.FirstOrDefault(m =>
            string.Equals(m.Name, needle, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var matches = monitors
            .Where(m => m.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                        || m.Id.ToString().StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"«{nameOrId}» подходит сразу нескольким мониторам: "
                + string.Join(", ", matches.Select(m => m.Name))
                + ". Уточни имя."),
        };
    }

    public async Task SaveAsync(Monitor monitor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO monitors (id, name, description, kind, subject, target_kind, target_value,
                                  target_label, parameters_json, thresholds_json, schedule_json,
                                  alert_json, objective_json, preset_id, enabled,
                                  created_ticks, updated_ticks, next_due_ticks)
            VALUES ($id, $name, $description, $kind, $subject, $targetKind, $targetValue,
                    $targetLabel, $parameters, $thresholds, $schedule,
                    $alert, $objective, $preset, $enabled,
                    $created, $updated, $due)
            ON CONFLICT(id) DO UPDATE SET
                name            = $name,
                description     = $description,
                kind            = $kind,
                subject         = $subject,
                target_kind     = $targetKind,
                target_value    = $targetValue,
                target_label    = $targetLabel,
                parameters_json = $parameters,
                thresholds_json = $thresholds,
                schedule_json   = $schedule,
                alert_json      = $alert,
                objective_json  = $objective,
                preset_id       = $preset,
                enabled         = $enabled,
                updated_ticks   = $updated,
                next_due_ticks  = $due;
            """;

        command.Parameters.AddWithValue("$id", monitor.Id.ToString());
        command.Parameters.AddWithValue("$name", monitor.Name);
        command.Parameters.AddWithValue("$description", (object?)monitor.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (int)monitor.Kind);
        command.Parameters.AddWithValue("$subject", monitor.Subject);
        command.Parameters.AddWithValue("$targetKind", (int)monitor.Target.Kind);
        command.Parameters.AddWithValue("$targetValue", monitor.Target.Value);
        command.Parameters.AddWithValue("$targetLabel", (object?)monitor.Target.Label ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$parameters",
            StorageJson.SerializeParameters(new Dictionary<string, string?>(monitor.Parameters, StringComparer.OrdinalIgnoreCase)));
        command.Parameters.AddWithValue("$thresholds", StorageJson.SerializeThresholds([.. monitor.Thresholds]));
        command.Parameters.AddWithValue("$schedule", StorageJson.SerializeSchedule(monitor.Schedule));
        command.Parameters.AddWithValue("$alert", (object?)StorageJson.SerializeAlertRule(monitor.Alert) ?? DBNull.Value);
        command.Parameters.AddWithValue("$objective", (object?)StorageJson.SerializeObjective(monitor.Objective) ?? DBNull.Value);
        command.Parameters.AddWithValue("$preset", (object?)monitor.PresetId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", monitor.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$created", monitor.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$updated", monitor.UpdatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$due", (object?)monitor.NextDueUtc?.UtcTicks ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM monitors WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task SetNextDueAsync(
        Guid id,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE monitors SET next_due_ticks = $due WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$due", (object?)nextDueUtc?.UtcTicks ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitorStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT state_level, last_run_ticks, last_summary, alert_state_json FROM monitors WHERE id = $id;";

        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MonitorStatus.Fresh;
        }

        return new MonitorStatus
        {
            Level = (VerdictLevel)reader.GetInt32(0),
            LastRunUtc = reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
            LastSummary = reader.IsDBNull(2) ? null : reader.GetString(2),
            Alert = StorageJson.DeserializeAlertState(reader.IsDBNull(3) ? null : reader.GetString(3)),
        };
    }

    public async Task SaveStatusAsync(
        Guid id,
        MonitorStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE monitors SET
                state_level      = $level,
                last_run_ticks   = $lastRun,
                last_summary     = $summary,
                alert_state_json = $alert
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$level", (int)status.Level);
        command.Parameters.AddWithValue("$lastRun", (object?)status.LastRunUtc?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)status.LastSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$alert", StorageJson.SerializeAlertState(status.Alert));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendCheckAsync(MonitorCheck check, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            INSERT INTO monitor_checks ({CheckColumns})
            VALUES ($id, $monitor, $started, $duration, $kind, $level, $summary,
                    $run, $metric, $value, $threshold, $missed, $error);
            """;

        command.Parameters.AddWithValue("$id", check.Id.ToString());
        command.Parameters.AddWithValue("$monitor", check.MonitorId.ToString());
        command.Parameters.AddWithValue("$started", check.StartedUtc.UtcTicks);
        command.Parameters.AddWithValue("$duration", check.Duration.Ticks);
        command.Parameters.AddWithValue("$kind", (int)check.Kind);
        command.Parameters.AddWithValue("$level", (int)check.Level);
        command.Parameters.AddWithValue("$summary", check.Summary);
        command.Parameters.AddWithValue("$run", (object?)check.RunId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$metric", (object?)check.Metric ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", (object?)check.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$threshold", (object?)check.Threshold ?? DBNull.Value);
        command.Parameters.AddWithValue("$missed", check.MissedCount);
        command.Parameters.AddWithValue("$error", (object?)check.Error ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitorCheck>> ListChecksAsync(
        CheckQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new List<string>();

        if (query.MonitorId is { } monitorId)
        {
            where.Add("monitor_id = $monitor");
            command.Parameters.AddWithValue("$monitor", monitorId.ToString());
        }

        if (query.Since is { } since)
        {
            where.Add("started_ticks >= $since");
            command.Parameters.AddWithValue("$since", since.UtcTicks);
        }

        if (query.Until is { } until)
        {
            where.Add("started_ticks <= $until");
            command.Parameters.AddWithValue("$until", until.UtcTicks);
        }

        var filter = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;

        command.CommandText =
            $"SELECT {CheckColumns} FROM monitor_checks{filter} ORDER BY started_ticks DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));

        var checks = new List<MonitorCheck>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            checks.Add(ReadCheck(reader));
        }

        return checks;
    }

    public async Task AppendAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            INSERT INTO alerts ({AlertColumns})
            VALUES ($id, $monitor, $name, $at, $action, $level, $reason, $summary,
                    $check, $notified, $channels, $errors);
            """;

        command.Parameters.AddWithValue("$id", alert.Id.ToString());
        command.Parameters.AddWithValue("$monitor", alert.MonitorId.ToString());
        command.Parameters.AddWithValue("$name", alert.MonitorName);
        command.Parameters.AddWithValue("$at", alert.AtUtc.UtcTicks);
        command.Parameters.AddWithValue("$action", (int)alert.Action);
        command.Parameters.AddWithValue("$level", (int)alert.Level);
        command.Parameters.AddWithValue("$reason", alert.Reason);
        command.Parameters.AddWithValue("$summary", (object?)alert.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$check", (object?)alert.CheckId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$notified", alert.Notified ? 1 : 0);
        command.Parameters.AddWithValue("$channels", StorageJson.SerializeTags([.. alert.Channels]));
        command.Parameters.AddWithValue("$errors", StorageJson.SerializeTags([.. alert.DeliveryErrors]));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertEvent>> ListAlertsAsync(
        AlertQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new List<string>();

        if (query.MonitorId is { } monitorId)
        {
            where.Add("monitor_id = $monitor");
            command.Parameters.AddWithValue("$monitor", monitorId.ToString());
        }

        if (query.Since is { } since)
        {
            where.Add("at_ticks >= $since");
            command.Parameters.AddWithValue("$since", since.UtcTicks);
        }

        if (query.NotifiedOnly)
        {
            where.Add("notified = 1");
        }

        var filter = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;

        command.CommandText = $"SELECT {AlertColumns} FROM alerts{filter} ORDER BY at_ticks DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));

        var alerts = new List<AlertEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            alerts.Add(ReadAlert(reader));
        }

        return alerts;
    }

    private static Monitor ReadMonitor(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Kind = (MonitorKind)reader.GetInt32(3),
        Subject = reader.GetString(4),
        Target = new Target
        {
            Kind = (TargetKind)reader.GetInt32(5),
            Value = reader.GetString(6),
            Label = reader.IsDBNull(7) ? null : reader.GetString(7),
        },
        Parameters = StorageJson.DeserializeParameters(reader.GetString(8)),
        Thresholds = StorageJson.DeserializeThresholds(reader.GetString(9)),

        // Расписание — единственное, без чего монитор бессмыслен. Строка, у которой
        // его не разобрать, испорчена, и молча подставлять сюда «раз в час» нельзя.
        Schedule = StorageJson.DeserializeSchedule(reader.GetString(10))
                   ?? throw new InvalidOperationException(
                       $"У монитора «{reader.GetString(1)}» не читается расписание."),

        Alert = StorageJson.DeserializeAlertRule(reader.IsDBNull(11) ? null : reader.GetString(11)),
        Objective = StorageJson.DeserializeObjective(reader.IsDBNull(12) ? null : reader.GetString(12)),
        PresetId = reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
        IsEnabled = reader.GetInt32(14) != 0,
        CreatedUtc = new DateTimeOffset(reader.GetInt64(15), TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(reader.GetInt64(16), TimeSpan.Zero),
        NextDueUtc = reader.IsDBNull(17) ? null : new DateTimeOffset(reader.GetInt64(17), TimeSpan.Zero),
    };

    private static MonitorCheck ReadCheck(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        MonitorId = Guid.Parse(reader.GetString(1)),
        StartedUtc = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
        Duration = TimeSpan.FromTicks(reader.GetInt64(3)),
        Kind = (CheckKind)reader.GetInt32(4),
        Level = (VerdictLevel)reader.GetInt32(5),
        Summary = reader.GetString(6),
        RunId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        Metric = reader.IsDBNull(8) ? null : reader.GetString(8),
        Value = reader.IsDBNull(9) ? null : reader.GetDouble(9),
        Threshold = reader.IsDBNull(10) ? null : reader.GetDouble(10),
        MissedCount = reader.GetInt32(11),
        Error = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private static AlertEvent ReadAlert(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        MonitorId = Guid.Parse(reader.GetString(1)),
        MonitorName = reader.GetString(2),
        AtUtc = new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
        Action = (AlertAction)reader.GetInt32(4),
        Level = (VerdictLevel)reader.GetInt32(5),
        Reason = reader.GetString(6),
        Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
        CheckId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
        Notified = reader.GetInt32(9) != 0,
        Channels = StorageJson.DeserializeTags(reader.IsDBNull(10) ? null : reader.GetString(10)),
        DeliveryErrors = StorageJson.DeserializeTags(reader.IsDBNull(11) ? null : reader.GetString(11)),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
