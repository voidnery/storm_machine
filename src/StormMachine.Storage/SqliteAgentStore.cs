using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Agents;

namespace StormMachine.Storage;

/// <summary>
/// Сопряжённые агенты и личность клиента в той же базе, что и журнал.
/// </summary>
/// <remarks>
/// В той же базе намеренно. Резервная копия одного файла обязана возвращать работающую
/// установку целиком: журнал без сопряжений оставил бы историю измерений, до агентов
/// которой уже не достучаться, а сопряжения без журнала — связь без истории.
/// </remarks>
public sealed class SqliteAgentStore(SqliteRunStore runStore) : IAgentStore
{
    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _runStore.InitializeAsync(cancellationToken);

    public async Task<byte[]?> LoadIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT container FROM client_identity WHERE id = 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (byte[])reader.GetValue(0);
    }

    public async Task SaveIdentityAsync(byte[] container, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO client_identity (id, container, created_ticks)
            VALUES (1, $container, $created)
            ON CONFLICT(id) DO UPDATE SET container = $container, created_ticks = $created;
            """;

        command.Parameters.AddWithValue("$container", container);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM agents ORDER BY paired_ticks;";

        var agents = new List<RemoteAgent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(Read(reader));
        }

        return agents;
    }

    /// <summary>
    /// Ищет агента по отпечатку, его началу, имени машины или псевдониму.
    /// </summary>
    /// <remarks>
    /// Оператор набирает то, что видит в списке, а видит он имя и восемь знаков отпечатка.
    /// Требовать полные шестьдесят четыре значило бы заставить копировать их каждый раз.
    /// Неоднозначное сокращение — ошибка, а не догадка: выбрать за человека, к какому
    /// из двух агентов он обращался, нельзя.
    /// <para>
    /// Адрес тоже принимается. Сопряжение начинается с адреса, и он же напечатан
    /// в подтверждении — отказать в нём через минуту после этого значило бы отвергнуть
    /// ровно то, что продукт сам только что показал.
    /// </para>
    /// </remarks>
    public async Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprintOrName);

        var needle = thumbprintOrName.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        var agents = await ListAsync(cancellationToken).ConfigureAwait(false);

        var matches = agents
            .Where(a => a.Thumbprint.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.MachineName, needle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Alias, needle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Address, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"«{thumbprintOrName}» подходит сразу нескольким агентам: "
                + string.Join(", ", matches.Select(m => $"{m.DisplayName} ({m.ShortThumbprint})"))
                + ". Уточни отпечаток."),
        };
    }

    public async Task SaveAsync(RemoteAgent agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO agents (thumbprint, machine_name, product, address, port, direction,
                                paired_ticks, last_seen_ticks, capabilities, alias)
            VALUES ($thumbprint, $machine, $product, $address, $port, $direction,
                    $paired, $seen, $capabilities, $alias)
            ON CONFLICT(thumbprint) DO UPDATE SET
                machine_name    = $machine,
                product         = $product,
                address         = $address,
                port            = $port,
                direction       = $direction,
                last_seen_ticks = $seen,
                capabilities    = $capabilities,
                alias           = COALESCE($alias, alias);
            """;

        command.Parameters.AddWithValue("$thumbprint", agent.Thumbprint);
        command.Parameters.AddWithValue("$machine", agent.MachineName);
        command.Parameters.AddWithValue("$product", agent.Product);
        command.Parameters.AddWithValue("$address", (object?)agent.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", agent.Port);
        command.Parameters.AddWithValue("$direction", (int)agent.Direction);
        command.Parameters.AddWithValue("$paired", agent.PairedUtc.UtcTicks);
        command.Parameters.AddWithValue("$seen", (object?)agent.LastSeenUtc?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$capabilities", string.Join(',', agent.Capabilities));
        command.Parameters.AddWithValue("$alias", (object?)agent.Alias ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ForgetAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM agents WHERE thumbprint = $thumbprint;";
        command.Parameters.AddWithValue("$thumbprint", thumbprint);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private const string Columns =
        "thumbprint, machine_name, product, address, port, direction, "
        + "paired_ticks, last_seen_ticks, capabilities, alias";

    private static RemoteAgent Read(SqliteDataReader reader) => new()
    {
        Thumbprint = reader.GetString(0),
        MachineName = reader.GetString(1),
        Product = reader.GetString(2),
        Address = reader.IsDBNull(3) ? null : reader.GetString(3),
        Port = reader.GetInt32(4),
        Direction = (AgentDirection)reader.GetInt32(5),
        PairedUtc = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
        LastSeenUtc = reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
        Capabilities = reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries),
        Alias = reader.IsDBNull(9) ? null : reader.GetString(9),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
