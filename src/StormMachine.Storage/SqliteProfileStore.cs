using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Profiles;

namespace StormMachine.Storage;

/// <summary>
/// Профили сетевого окружения в той же базе.
/// </summary>
/// <remarks>
/// Активный профиль хранится флагом в строке, а не отдельной настройкой: так
/// «активен ровно один» держится уникальным индексом базы, а не аккуратностью
/// вызывающего кода.
/// </remarks>
public sealed class SqliteProfileStore(SqliteRunStore runStore) : IProfileStore
{
    private const string Columns =
        "id, name, description, targets_json, thresholds_json, monitors_json, "
        + "signature_json, is_active, created_ticks, updated_ticks";

    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public async Task<IReadOnlyList<NetworkProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM profiles ORDER BY name;";

        var profiles = new List<NetworkProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(Read(reader));
        }

        return profiles;
    }

    public async Task<NetworkProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<NetworkProfile?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM profiles WHERE is_active = 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    /// <remarks>
    /// Точное имя выигрывает у совпадения по началу; неоднозначное сокращение —
    /// ошибка, а не догадка. То же правило, что у агентов, мониторов и эталонов.
    /// </remarks>
    public async Task<NetworkProfile?> FindAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        var needle = nameOrId.Trim();
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);

        var exact = all.FirstOrDefault(p => string.Equals(p.Name, needle, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var matches = all
            .Where(p => p.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                        || p.Id.ToString().StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"«{nameOrId}» подходит сразу нескольким профилям: "
                + string.Join(", ", matches.Select(m => m.Name))
                + ". Уточни имя."),
        };
    }

    public async Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO profiles (id, name, description, targets_json, thresholds_json,
                                  monitors_json, signature_json, is_active,
                                  created_ticks, updated_ticks)
            VALUES ($id, $name, $description, $targets, $thresholds,
                    $monitors, $signature, $active, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name            = $name,
                description     = $description,
                targets_json    = $targets,
                thresholds_json = $thresholds,
                monitors_json   = $monitors,
                signature_json  = $signature,
                updated_ticks   = $updated;
            """;

        command.Parameters.AddWithValue("$id", profile.Id.ToString());
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$description", (object?)profile.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$targets", StorageJson.SerializeTags([.. profile.Targets]));
        command.Parameters.AddWithValue("$thresholds", StorageJson.SerializeThresholds([.. profile.Thresholds]));
        command.Parameters.AddWithValue(
            "$monitors",
            StorageJson.SerializeTags([.. profile.Monitors.Select(m => m.ToString())]));
        command.Parameters.AddWithValue("$signature", StorageJson.SerializeSignature(profile.Signature));

        // Активность через этот путь не меняется: для неё есть ActivateAsync,
        // который снимает флаг со всех остальных одной транзакцией.
        command.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$created", profile.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$updated", profile.UpdatedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Делает профиль активным.
    /// </summary>
    /// <remarks>
    /// Снятие и установка идут одной транзакцией: между ними база не должна
    /// оказаться в состоянии «активных нет» или «активных два», даже на мгновение.
    /// Уникальный индекс всё равно не позволил бы второго, но упал бы посреди работы.
    /// </remarks>
    public async Task ActivateAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE profiles SET is_active = 0 WHERE is_active = 1;";

            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (id is { } chosen)
        {
            await using var set = connection.CreateCommand();

            set.Transaction = (SqliteTransaction)transaction;
            set.CommandText = "UPDATE profiles SET is_active = 1 WHERE id = $id;";
            set.Parameters.AddWithValue("$id", chosen.ToString());

            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static NetworkProfile Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Targets = StorageJson.DeserializeTags(reader.GetString(3)),
        Thresholds = StorageJson.DeserializeThresholds(reader.GetString(4)),
        Monitors =
        [
            .. StorageJson.DeserializeTags(reader.GetString(5))
                .Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty),
        ],
        Signature = StorageJson.DeserializeSignature(reader.GetString(6)),
        IsActive = reader.GetInt32(7) != 0,
        CreatedUtc = new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
