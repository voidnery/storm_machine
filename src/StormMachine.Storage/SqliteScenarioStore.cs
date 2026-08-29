using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Storage;

/// <summary>
/// Сценарии оператора в той же базе, что и всё остальное.
/// </summary>
/// <remarks>
/// Шаги лежат одним полем JSON, а не отдельной таблицей. Причина та же, по которой
/// параметры пресета хранятся строками: их состав задаёт проба своим объявлением,
/// и хранилищу незачем знать, что у HTTP есть метод, а у ICMP — TTL. Разложить это
/// по колонкам значило бы завести схему, устаревающую с каждой новой пробой.
/// </remarks>
public sealed class SqliteScenarioStore(SqliteRunStore runStore) : IScenarioStore
{
    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _runStore.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, name, description, steps_json, version, created_ticks, updated_ticks
              FROM scenarios
             ORDER BY name;
            """;

        var found = new List<Scenario>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public async Task<Scenario?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, name, description, steps_json, version, created_ticks, updated_ticks
              FROM scenarios
             WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>
    /// Находит по имени или началу идентификатора.
    /// </summary>
    /// <remarks>
    /// Сначала точное имя, потом начало идентификатора — тот же порядок, что у пресетов
    /// и мониторов. Оператор набирает имя, а идентификатор копирует из вывода, и
    /// перепутать их нельзя: имена не выглядят как GUID.
    /// </remarks>
    public async Task<Scenario?> FindAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        var text = nameOrId.Trim();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, name, description, steps_json, version, created_ticks, updated_ticks
              FROM scenarios
             WHERE name_key = $key OR id LIKE $prefix
             ORDER BY CASE WHEN name_key = $key THEN 0 ELSE 1 END
             LIMIT 1;
            """;

        command.Parameters.AddWithValue("$key", text.ToUpperInvariant());
        command.Parameters.AddWithValue("$prefix", text + "%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task SaveAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO scenarios
                (id, name, name_key, description, steps_json, version, created_ticks, updated_ticks)
            VALUES
                ($id, $name, $key, $description, $steps, $version, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name          = excluded.name,
                name_key      = excluded.name_key,
                description   = excluded.description,
                steps_json    = excluded.steps_json,
                version       = excluded.version,
                updated_ticks = excluded.updated_ticks;
            """;

        command.Parameters.AddWithValue("$id", scenario.Id.ToString());
        command.Parameters.AddWithValue("$name", scenario.Name);
        command.Parameters.AddWithValue("$key", scenario.Name.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$description", (object?)scenario.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$steps", StorageJson.SerializeSteps([.. scenario.Steps]));
        command.Parameters.AddWithValue("$version", scenario.Version);
        command.Parameters.AddWithValue("$created", scenario.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$updated", scenario.UpdatedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM scenarios WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static Scenario Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Steps = StorageJson.DeserializeSteps(reader.GetString(3)),
        Version = reader.GetInt32(4),
        CreatedUtc = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
    };

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
