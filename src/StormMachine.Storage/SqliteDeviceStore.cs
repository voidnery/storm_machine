using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.Storage;

/// <summary>
/// Инвентарь на SQLite: сканирования, устройства, свидетельства, журнал действий.
/// </summary>
/// <remarks>
/// Делит файл с журналом прогонов и библиотекой пресетов. Заводить вторую базу
/// значило бы получить два места, которые надо раздельно чинить, переносить
/// и подчищать, — ради разделения, которого никто не просил.
/// <para>
/// Хранит два представления. Снимок сканирования неизменяем: по нему считаются
/// различия, и переписывать его задним числом нельзя. Сводный инвентарь, наоборот,
/// пересчитывается — в нём живёт всё, что мы когда-либо узнали об устройстве,
/// включая правку оператора.
/// </para>
/// </remarks>
public sealed class SqliteDeviceStore : IDeviceStore
{
    private readonly SqliteRunStore _runs;

    public SqliteDeviceStore(SqliteRunStore runs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _runs.InitializeAsync(cancellationToken);

    public async Task SaveScanAsync(DiscoveryScan scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO scans (id, range_text, interface_name, started_ticks, completed_ticks, probed, cancelled)
                VALUES ($id, $range, $interface, $started, $completed, $probed, $cancelled);
                """;

            command.Parameters.AddWithValue("$id", scan.Id.ToString());
            command.Parameters.AddWithValue("$range", scan.Range);
            command.Parameters.AddWithValue("$interface", scan.InterfaceName);
            command.Parameters.AddWithValue("$started", scan.StartedUtc.UtcTicks);
            command.Parameters.AddWithValue("$completed", scan.CompletedUtc?.UtcTicks ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$probed", scan.Probed);
            command.Parameters.AddWithValue("$cancelled", scan.WasCancelled ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteSnapshotAsync(connection, (SqliteTransaction)transaction, scan, cancellationToken).ConfigureAwait(false);
        await MarkAbsentAsync(connection, (SqliteTransaction)transaction, scan, cancellationToken).ConfigureAwait(false);
        await MergeInventoryAsync(connection, (SqliteTransaction)transaction, scan, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoveryScan scan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_devices (scan_id, ordinal, address, identity, is_online, evidence_json)
            VALUES ($scan, $ordinal, $address, $identity, $online, $evidence);
            """;

        var scanParameter = command.Parameters.Add("$scan", SqliteType.Text);
        var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var addressParameter = command.Parameters.Add("$address", SqliteType.Text);
        var identityParameter = command.Parameters.Add("$identity", SqliteType.Text);
        var onlineParameter = command.Parameters.Add("$online", SqliteType.Integer);
        var evidenceParameter = command.Parameters.Add("$evidence", SqliteType.Text);

        scanParameter.Value = scan.Id.ToString();

        for (var i = 0; i < scan.Devices.Count; i++)
        {
            var device = scan.Devices[i];

            ordinalParameter.Value = i;
            addressParameter.Value = device.Address;
            identityParameter.Value = device.Identity;
            onlineParameter.Value = device.IsOnline ? 1 : 0;
            evidenceParameter.Value = StorageJson.SerializeEvidence([.. device.Evidence]);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Снимает отметку доступности с устройств, которых в этом сканировании не нашлось.
    /// </summary>
    /// <remarks>
    /// Только внутри просканированного диапазона: за его пределами мы не смотрели,
    /// и объявлять тамошние устройства недоступными значило бы утверждать то,
    /// чего не проверяли. Без этой оговорки инвентарь после сканирования одной подсети
    /// сообщал бы, что вся сеть легла.
    /// </remarks>
    private static async Task MarkAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoveryScan scan,
        CancellationToken cancellationToken)
    {
        AddressRange range;

        try
        {
            range = AddressRange.Parse(scan.Range);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Диапазон записан в непонятном виде — лучше не трогать ничего,
            // чем пометить недоступным то, что не проверяли.
            return;
        }

        var seen = new HashSet<string>(scan.Devices.Select(d => d.Identity), StringComparer.OrdinalIgnoreCase);
        var absent = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            // Все адреса устройства, а не только основной: узел может занимать
            // адреса в нескольких подсетях, и решать о нём по одному из них неверно.
            command.CommandText = """
                SELECT d.identity, a.address
                  FROM devices d
                  JOIN device_addresses a ON a.identity = d.identity
                 WHERE d.is_online = 1;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = reader.GetString(0);

                if (seen.Contains(identity) || absent.Contains(identity))
                {
                    continue;
                }

                if (System.Net.IPAddress.TryParse(reader.GetString(1), out var address) && range.Contains(address))
                {
                    absent.Add(identity);
                }
            }
        }

        if (absent.Count == 0)
        {
            return;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE devices SET is_online = 0 WHERE identity = $identity;";

        var identityParameter = update.Parameters.Add("$identity", SqliteType.Text);

        foreach (var identity in absent)
        {
            identityParameter.Value = identity;
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Вливает наблюдения сканирования в сводный инвентарь.
    /// </summary>
    /// <remarks>
    /// Свидетельства именно вливаются, а не заменяют прежние: устройство, которое сегодня
    /// не ответило, не должно потерять всё, что мы о нём знали. Меняется лишь отметка
    /// доступности и время последнего наблюдения.
    /// </remarks>
    private static async Task MergeInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoveryScan scan,
        CancellationToken cancellationToken)
    {
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = """
            INSERT INTO devices (identity, address, first_seen_ticks, last_seen_ticks, is_online)
            VALUES ($identity, $address, $seen, $seen, $online)
            ON CONFLICT (identity) DO UPDATE SET
                address         = MIN(devices.address, excluded.address),
                last_seen_ticks = MAX(devices.last_seen_ticks, excluded.last_seen_ticks),
                is_online       = excluded.is_online;
            """;

        var identityParameter = device.Parameters.Add("$identity", SqliteType.Text);
        var addressParameter = device.Parameters.Add("$address", SqliteType.Text);
        var seenParameter = device.Parameters.Add("$seen", SqliteType.Integer);
        var onlineParameter = device.Parameters.Add("$online", SqliteType.Integer);

        await using var addresses = connection.CreateCommand();
        addresses.Transaction = transaction;
        addresses.CommandText = """
            INSERT INTO device_addresses (identity, address, last_seen_ticks)
            VALUES ($identity, $address, $seen)
            ON CONFLICT (identity, address) DO UPDATE SET
                last_seen_ticks = MAX(device_addresses.last_seen_ticks, excluded.last_seen_ticks);
            """;

        var addressIdentity = addresses.Parameters.Add("$identity", SqliteType.Text);
        var addressValue = addresses.Parameters.Add("$address", SqliteType.Text);
        var addressSeen = addresses.Parameters.Add("$seen", SqliteType.Integer);

        await using var evidence = connection.CreateCommand();
        evidence.Transaction = transaction;
        evidence.CommandText = """
            INSERT INTO device_evidence (identity, source, kind, value, observed_ticks)
            VALUES ($identity, $source, $kind, $value, $observed)
            ON CONFLICT (identity, source, kind, value) DO UPDATE SET
                observed_ticks = MAX(device_evidence.observed_ticks, excluded.observed_ticks);
            """;

        var evidenceIdentity = evidence.Parameters.Add("$identity", SqliteType.Text);
        var sourceParameter = evidence.Parameters.Add("$source", SqliteType.Integer);
        var kindParameter = evidence.Parameters.Add("$kind", SqliteType.Integer);
        var valueParameter = evidence.Parameters.Add("$value", SqliteType.Text);
        var observedParameter = evidence.Parameters.Add("$observed", SqliteType.Integer);

        foreach (var item in scan.Devices)
        {
            identityParameter.Value = item.Identity;
            addressParameter.Value = item.Address;
            seenParameter.Value = item.LastSeenUtc.UtcTicks;
            onlineParameter.Value = item.IsOnline ? 1 : 0;

            await device.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            addressIdentity.Value = item.Identity;
            addressValue.Value = item.Address;
            addressSeen.Value = item.LastSeenUtc.UtcTicks;

            await addresses.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            evidenceIdentity.Value = item.Identity;

            foreach (var fact in item.Evidence)
            {
                sourceParameter.Value = (int)fact.Source;
                kindParameter.Value = (int)fact.Kind;
                valueParameter.Value = fact.Value;
                observedParameter.Value = fact.ObservedUtc.UtcTicks;

                await evidence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<DiscoveryScan>> ListScansAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT s.id, s.range_text, s.interface_name, s.started_ticks, s.completed_ticks,
                   s.probed, s.cancelled,
                   (SELECT COUNT(*) FROM scan_devices d WHERE d.scan_id = s.id AND d.is_online = 1)
              FROM scans s
             ORDER BY s.started_ticks DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var scans = new List<DiscoveryScan>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Устройства в список не читаются: строк на экране двадцать, а устройств
            // в каждом сканировании — сотни. Число ответивших приходит подзапросом.
            scans.Add(ReadScan(reader, online: reader.GetInt32(7)));
        }

        return scans;
    }

    public async Task<DiscoveryScan?> GetScanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        DiscoveryScan? scan = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, range_text, interface_name, started_ticks, completed_ticks, probed, cancelled
                  FROM scans
                 WHERE id = $id OR id LIKE $prefix
                 LIMIT 1;
                """;

            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$prefix", id.ToString() + "%");

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scan = ReadScan(reader, online: 0);
            }
        }

        if (scan is null)
        {
            return null;
        }

        return scan with { Devices = await ReadSnapshotAsync(connection, scan.Id, cancellationToken).ConfigureAwait(false) };
    }

    private static async Task<List<Device>> ReadSnapshotAsync(
        SqliteConnection connection,
        Guid scanId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT address, is_online, evidence_json
              FROM scan_devices
             WHERE scan_id = $scan
             ORDER BY ordinal;
            """;

        command.Parameters.AddWithValue("$scan", scanId.ToString());

        var devices = new List<Device>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evidence = StorageJson.DeserializeEvidence(reader.GetString(2));
            var observed = evidence.Length > 0 ? evidence.Min(e => e.ObservedUtc) : DateTimeOffset.UtcNow;

            devices.Add(Device.FromEvidence(
                reader.GetString(0),
                evidence,
                firstSeenUtc: observed,
                lastSeenUtc: observed,
                isOnline: reader.GetInt32(1) == 1));
        }

        return devices;
    }

    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var evidence = new Dictionary<string, List<Evidence>>(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT identity, source, kind, value, observed_ticks FROM device_evidence;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = reader.GetString(0);

                if (!evidence.TryGetValue(identity, out var bucket))
                {
                    bucket = [];
                    evidence[identity] = bucket;
                }

                bucket.Add(new Evidence
                {
                    Source = (EvidenceSource)reader.GetInt32(1),
                    Kind = (EvidenceKind)reader.GetInt32(2),
                    Value = reader.GetString(3),
                    ObservedUtc = new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                });
            }
        }

        var addresses = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT identity, address FROM device_addresses ORDER BY address;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = reader.GetString(0);

                if (!addresses.TryGetValue(identity, out var bucket))
                {
                    bucket = [];
                    addresses[identity] = bucket;
                }

                bucket.Add(reader.GetString(1));
            }
        }

        await using var devices = connection.CreateCommand();
        devices.CommandText = """
            SELECT identity, address, first_seen_ticks, last_seen_ticks, is_online
              FROM devices
             ORDER BY last_seen_ticks DESC;
            """;

        var result = new List<Device>();
        await using var deviceReader = await devices.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await deviceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var identity = deviceReader.GetString(0);

            var device = Device.FromEvidence(
                deviceReader.GetString(1),
                evidence.TryGetValue(identity, out var bucket) ? bucket : [],
                firstSeenUtc: new DateTimeOffset(deviceReader.GetInt64(2), TimeSpan.Zero),
                lastSeenUtc: new DateTimeOffset(deviceReader.GetInt64(3), TimeSpan.Zero),
                isOnline: deviceReader.GetInt32(4) == 1);

            result.Add(addresses.TryGetValue(identity, out var known)
                ? device with { Addresses = known }
                : device);
        }

        return result;
    }

    public async Task PinAsync(string identity, Evidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(evidence);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Прежняя правка того же поля удаляется: у одного поля один хозяин,
        // иначе две правки подряд оставили бы в базе противоречие,
        // которое разрешалось бы сравнением строк.
        command.CommandText = """
            DELETE FROM device_evidence
             WHERE identity = $identity AND source = $source AND kind = $kind;

            INSERT INTO device_evidence (identity, source, kind, value, observed_ticks)
            VALUES ($identity, $source, $kind, $value, $observed);
            """;

        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$source", (int)EvidenceSource.Manual);
        command.Parameters.AddWithValue("$kind", (int)evidence.Kind);
        command.Parameters.AddWithValue("$value", evidence.Value);
        command.Parameters.AddWithValue("$observed", evidence.ObservedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO audit (id, at_ticks, action, target, operator_name, details)
            VALUES ($id, $at, $action, $target, $operator, $details);
            """;

        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$at", entry.AtUtc.UtcTicks);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$operator", entry.Operator);
        command.Parameters.AddWithValue("$details", (object?)entry.Details ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, at_ticks, action, target, operator_name, details
              FROM audit
             ORDER BY at_ticks DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new AuditEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                AtUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                Action = reader.GetString(2),
                Target = reader.GetString(3),
                Operator = reader.GetString(4),
                Details = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return entries;
    }

    private static DiscoveryScan ReadScan(SqliteDataReader reader, int online)
    {
        _ = online;

        return new DiscoveryScan
        {
            Id = Guid.Parse(reader.GetString(0)),
            Range = reader.GetString(1),
            InterfaceName = reader.GetString(2),
            StartedUtc = new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
            CompletedUtc = reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
            Probed = reader.GetInt32(5),
            WasCancelled = reader.GetInt32(6) == 1,
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_runs.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
