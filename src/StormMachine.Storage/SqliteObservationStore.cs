using System.Globalization;
using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.Storage;

/// <summary>
/// История наблюдений за оборудованием в той же базе, что и всё остальное.
/// </summary>
/// <remarks>
/// В той же базе намеренно: наблюдения читают вместе с измерениями и вместе с ними же
/// убирают по политике хранения. Отдельный файл означал бы вторую политику, второй путь
/// и второй способ его потерять.
/// </remarks>
public sealed class SqliteObservationStore(SqliteRunStore runStore) : IObservationStore
{
    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _runStore.InitializeAsync(cancellationToken);

    // ------------------------------------------------------------ загрузка портов

    public async Task SavePortLoadAsync(
        IReadOnlyList<PortLoadPoint> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // REPLACE, а не INSERT: повторный опрос в ту же миллисекунду — не ошибка,
        // и падать на нём незачем.
        command.CommandText = """
            INSERT OR REPLACE INTO port_load
                (device, if_index, at_ticks, if_name, interval_ms, in_bps, out_bps,
                 speed_bps, in_errors, out_errors, in_discards, out_discards)
            VALUES
                ($device, $index, $at, $name, $interval, $in, $out,
                 $speed, $inErr, $outErr, $inDisc, $outDisc);
            """;

        var device = command.Parameters.Add("$device", SqliteType.Text);
        var index = command.Parameters.Add("$index", SqliteType.Integer);
        var at = command.Parameters.Add("$at", SqliteType.Integer);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var interval = command.Parameters.Add("$interval", SqliteType.Integer);
        var inBps = command.Parameters.Add("$in", SqliteType.Real);
        var outBps = command.Parameters.Add("$out", SqliteType.Real);
        var speed = command.Parameters.Add("$speed", SqliteType.Integer);
        var inErr = command.Parameters.Add("$inErr", SqliteType.Integer);
        var outErr = command.Parameters.Add("$outErr", SqliteType.Integer);
        var inDisc = command.Parameters.Add("$inDisc", SqliteType.Integer);
        var outDisc = command.Parameters.Add("$outDisc", SqliteType.Integer);

        foreach (var point in points)
        {
            device.Value = point.Device;
            index.Value = point.IfIndex;
            at.Value = point.AtUtc.UtcTicks;
            name.Value = (object?)point.IfName ?? DBNull.Value;
            interval.Value = (long)point.Interval.TotalMilliseconds;
            inBps.Value = point.InBitsPerSecond;
            outBps.Value = point.OutBitsPerSecond;
            speed.Value = point.SpeedBitsPerSecond;
            inErr.Value = point.InErrors;
            outErr.Value = point.OutErrors;
            inDisc.Value = point.InDiscards;
            outDisc.Value = point.OutDiscards;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PortLoadPoint>> ListPortLoadAsync(
        string? device,
        int? ifIndex,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new List<string> { "at_ticks >= $since" };

        if (!string.IsNullOrWhiteSpace(device))
        {
            where.Add("device = $device");
            command.Parameters.AddWithValue("$device", device.Trim());
        }

        if (ifIndex is { } port)
        {
            where.Add("if_index = $index");
            command.Parameters.AddWithValue("$index", port);
        }

        command.CommandText = $"""
            SELECT device, if_index, at_ticks, if_name, interval_ms, in_bps, out_bps,
                   speed_bps, in_errors, out_errors, in_discards, out_discards
              FROM port_load
             WHERE {string.Join(" AND ", where)}
             ORDER BY at_ticks;
            """;

        command.Parameters.AddWithValue("$since", since.UtcTicks);

        var points = new List<PortLoadPoint>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(new PortLoadPoint
            {
                Device = reader.GetString(0),
                IfIndex = reader.GetInt32(1),
                AtUtc = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                IfName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Interval = TimeSpan.FromMilliseconds(reader.GetInt64(4)),
                InBitsPerSecond = reader.GetDouble(5),
                OutBitsPerSecond = reader.GetDouble(6),
                SpeedBitsPerSecond = reader.GetInt64(7),
                InErrors = reader.GetInt64(8),
                OutErrors = reader.GetInt64(9),
                InDiscards = reader.GetInt64(10),
                OutDiscards = reader.GetInt64(11),
            });
        }

        return points;
    }

    // ------------------------------------------------------------ услышанное

    public async Task SaveCaptureAsync(CaptureResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var neighbor in result.Neighbors)
        {
            await SaveNeighborAsync(connection, result, neighbor, cancellationToken).ConfigureAwait(false);
        }

        foreach (var sighting in result.Dhcp.Sightings)
        {
            await SaveDhcpAsync(connection, sighting, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveNeighborAsync(
        SqliteConnection connection,
        CaptureResult result,
        LinkNeighbor neighbor,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Время первого наблюдения не трогается при повторе: оно и есть ответ
        // на «когда появился», ради которого история и ведётся.
        command.CommandText = """
            INSERT INTO heard_neighbors
                (local_if, chassis, port_id, system_name, port_name, protocol, first_seen, last_seen)
            VALUES
                ($local, $chassis, $port, $name, $portName, $protocol, $seen, $seen)
            ON CONFLICT (local_if, chassis, port_id) DO UPDATE SET
                system_name = excluded.system_name,
                port_name   = excluded.port_name,
                last_seen   = excluded.last_seen;
            """;

        command.Parameters.AddWithValue("$local", result.Adapter.DisplayName);
        command.Parameters.AddWithValue("$chassis", neighbor.RemoteChassisId ?? string.Empty);
        command.Parameters.AddWithValue("$port", neighbor.RemotePort ?? string.Empty);
        command.Parameters.AddWithValue("$name", (object?)neighbor.RemoteName ?? DBNull.Value);
        command.Parameters.AddWithValue("$portName", (object?)neighbor.RemotePortDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$protocol", (int)neighbor.Protocol);
        command.Parameters.AddWithValue("$seen", neighbor.ObservedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveDhcpAsync(
        SqliteConnection connection,
        DhcpSighting sighting,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO heard_dhcp
                (server, offered_gw, server_mac, offered_dns, first_seen, last_seen, sightings)
            VALUES
                ($server, $gateway, $mac, $dns, $seen, $seen, 1)
            ON CONFLICT (server, offered_gw) DO UPDATE SET
                server_mac  = COALESCE(excluded.server_mac, heard_dhcp.server_mac),
                offered_dns = excluded.offered_dns,
                last_seen   = excluded.last_seen,
                sightings   = heard_dhcp.sightings + 1;
            """;

        command.Parameters.AddWithValue("$server", sighting.ServerAddress);

        // Пустая строка вместо NULL: колонка входит в первичный ключ, а NULL в ключе
        // SQLite считает не равным самому себе — и каждое наблюдение сервера,
        // не объявившего шлюз, заводило бы новую строку.
        command.Parameters.AddWithValue("$gateway", sighting.OfferedGateway ?? string.Empty);
        command.Parameters.AddWithValue("$mac", (object?)sighting.ServerMac ?? DBNull.Value);
        command.Parameters.AddWithValue("$dns", string.Join(',', sighting.OfferedDns));
        command.Parameters.AddWithValue("$seen", sighting.ObservedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HeardNeighbor>> ListNeighborsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT local_if, chassis, port_id, system_name, port_name, protocol, first_seen, last_seen
              FROM heard_neighbors
             WHERE last_seen >= $since
             ORDER BY last_seen DESC;
            """;

        command.Parameters.AddWithValue("$since", since.UtcTicks);

        var found = new List<HeardNeighbor>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(new HeardNeighbor
            {
                LocalInterface = reader.GetString(0),
                ChassisId = reader.GetString(1),
                PortId = reader.GetString(2),
                SystemName = reader.IsDBNull(3) ? null : reader.GetString(3),
                PortName = reader.IsDBNull(4) ? null : reader.GetString(4),
                Protocol = (NeighborProtocol)reader.GetInt32(5),
                FirstSeenUtc = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                LastSeenUtc = new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
            });
        }

        return found;
    }

    public async Task<IReadOnlyList<HeardDhcpServer>> ListDhcpAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT server, offered_gw, server_mac, offered_dns, first_seen, last_seen, sightings
              FROM heard_dhcp
             WHERE last_seen >= $since
             ORDER BY first_seen;
            """;

        command.Parameters.AddWithValue("$since", since.UtcTicks);

        var found = new List<HeardDhcpServer>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dns = reader.GetString(3);

            found.Add(new HeardDhcpServer
            {
                ServerAddress = reader.GetString(0),
                OfferedGateway = reader.GetString(1),
                ServerMac = reader.IsDBNull(2) ? null : reader.GetString(2),
                OfferedDns = dns.Length == 0 ? [] : [.. dns.Split(',')],
                FirstSeenUtc = new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                LastSeenUtc = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                Sightings = reader.GetInt32(6),
            });
        }

        return found;
    }

    // ------------------------------------------------------------------- уборка

    public async Task<int> ApplyRetentionAsync(
        TimeSpan horizon,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - horizon;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var removed = 0;

        // Ряд загрузки убирается по времени точки: это временной ряд и ничем
        // не отличается от сэмплов измерений.
        removed += await DeleteAsync(connection, "port_load", "at_ticks", cutoff, cancellationToken)
            .ConfigureAwait(false);

        // Соседи и серверы — по времени ПОСЛЕДНЕГО наблюдения, а не первого.
        // Сосед, услышанный год назад и слышимый до сих пор, — это действующее
        // соседство, и удалять его как старое было бы неверно.
        removed += await DeleteAsync(connection, "heard_neighbors", "last_seen", cutoff, cancellationToken)
            .ConfigureAwait(false);

        removed += await DeleteAsync(connection, "heard_dhcp", "last_seen", cutoff, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return removed;
    }

    private static async Task<int> DeleteAsync(
        SqliteConnection connection,
        string table,
        string column,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Имена таблицы и колонки — из кода, а не из ввода: параметризовать их
        // SQLite не позволяет, и подставлять сюда что-либо снаружи нельзя.
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"DELETE FROM {table} WHERE {column} < $cutoff;");

        command.Parameters.AddWithValue("$cutoff", cutoff.UtcTicks);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_runStore.Location}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
