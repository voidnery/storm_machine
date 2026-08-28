using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Presets;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage;

/// <summary>
/// Библиотека пресетов в той же базе, что и журнал прогонов.
/// </summary>
/// <remarks>
/// Пресет и его результаты — части одной истории. Разносить их по разным файлам значило бы
/// усложнить перенос и резервную копию ради несуществующей выгоды.
/// </remarks>
public sealed class SqlitePresetStore(SqliteRunStore runStore) : IPresetStore
{
    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public async Task<IReadOnlyList<Preset>> ListAsync(PresetQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            filters.Add("probe_name = $probe");
            command.Parameters.AddWithValue("$probe", query.Subject);
        }

        // Поиск по имени, описанию и тегам делается в памяти, а не в SQL.
        // Причина не в удобстве: функция LOWER() в SQLite приводит к нижнему регистру
        // только латиницу, и «Шлюз» никогда не совпал бы с «шлюз». Пресетов сотни,
        // так что цена такой фильтрации незаметна, а поведение — предсказуемо.
        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

        command.CommandText = $"""
            SELECT id, name, description, probe_name, target_kind, target_value, target_label,
                   parameters_json, tags_json, version, created_ticks, updated_ticks,
                   run_count, last_run_ticks, kind
              FROM presets
              {where}
             ORDER BY name_key
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));

        var result = new List<Preset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var preset = Read(reader);

            if (!Matches(preset, query))
            {
                continue;
            }

            result.Add(preset);
        }

        return result;
    }

    private static bool Matches(Preset preset, PresetQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Tag)
            && !preset.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return true;
        }

        return preset.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
               || (preset.Description?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await FindAsync("id = $key", id.ToString(), cancellationToken).ConfigureAwait(false);

    public async Task<Preset?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await FindAsync("name_key = $key", NameKey(name), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Preset> SaveAsync(Preset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var existing = await GetAsync(preset.Id, cancellationToken).ConfigureAwait(false);

        // Версия растёт только при изменении того, что влияет на измерение.
        // Переименование или правка описания версию не трогают — иначе счётчик
        // версий перестал бы что-либо значить.
        var version = existing is null
            ? 1
            : existing.IsSameMeasurement(preset) ? existing.Version : existing.Version + 1;

        var now = DateTimeOffset.UtcNow;

        var stored = preset with
        {
            Version = version,
            CreatedUtc = existing?.CreatedUtc ?? preset.CreatedUtc,
            UpdatedUtc = now,
            RunCount = existing?.RunCount ?? preset.RunCount,
            LastRunUtc = existing?.LastRunUtc ?? preset.LastRunUtc,
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO presets
                (id, name, name_key, description, probe_name, target_kind, target_value, target_label,
                 parameters_json, tags_json, version, created_ticks, updated_ticks, run_count,
                 last_run_ticks, kind)
            VALUES
                ($id, $name, $key, $description, $probe, $targetKind, $targetValue, $targetLabel,
                 $parameters, $tags, $version, $created, $updated, $runCount, $lastRun, $kind)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                name_key = excluded.name_key,
                description = excluded.description,
                probe_name = excluded.probe_name,
                kind = excluded.kind,
                target_kind = excluded.target_kind,
                target_value = excluded.target_value,
                target_label = excluded.target_label,
                parameters_json = excluded.parameters_json,
                tags_json = excluded.tags_json,
                version = excluded.version,
                updated_ticks = excluded.updated_ticks;
            """;

        command.Parameters.AddWithValue("$id", stored.Id.ToString());
        command.Parameters.AddWithValue("$name", stored.Name);
        command.Parameters.AddWithValue("$key", NameKey(stored.Name));
        command.Parameters.AddWithValue("$description", (object?)stored.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$probe", stored.Subject);
        command.Parameters.AddWithValue("$kind", (int)stored.Kind);
        command.Parameters.AddWithValue("$targetKind", (int)stored.Target.Kind);
        command.Parameters.AddWithValue("$targetValue", stored.Target.Value);
        command.Parameters.AddWithValue("$targetLabel", (object?)stored.Target.Label ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$parameters",
            StorageJson.SerializeParameters(new Dictionary<string, string?>(stored.Parameters, StringComparer.OrdinalIgnoreCase)));
        command.Parameters.AddWithValue("$tags", StorageJson.SerializeTags([.. stored.Tags]));
        command.Parameters.AddWithValue("$version", stored.Version);
        command.Parameters.AddWithValue("$created", stored.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$updated", stored.UpdatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$runCount", stored.RunCount);
        command.Parameters.AddWithValue(
            "$lastRun",
            stored.LastRunUtc.HasValue ? stored.LastRunUtc.Value.UtcTicks : DBNull.Value);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Нарушение уникальности имени. Сообщение по умолчанию говорит про индекс,
            // а оператору нужно понимать, что именно он сделал не так.
            throw new InvalidOperationException(
                $"Пресет с именем «{stored.Name}» уже есть в библиотеке. Выбери другое имя или отредактируй существующий.",
                ex);
        }

        return stored;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM presets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task RecordRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE presets
               SET run_count = run_count + 1,
                   last_run_ticks = $now
             WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT tags_json FROM presets;";

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var tag in StorageJson.DeserializeTags(reader.GetString(0)))
            {
                tags.Add(tag);
            }
        }

        return [.. tags];
    }

    private async Task<Preset?> FindAsync(string where, string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT id, name, description, probe_name, target_kind, target_value, target_label,
                   parameters_json, tags_json, version, created_ticks, updated_ticks,
                   run_count, last_run_ticks, kind
              FROM presets
             WHERE {where};
            """;

        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        // Схема общая с журналом: она уже создана и обновлена при инициализации.
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static Preset Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Subject = reader.GetString(3),
        Target = new Target
        {
            Kind = (TargetKind)reader.GetInt32(4),
            Value = reader.GetString(5),
            Label = reader.IsDBNull(6) ? null : reader.GetString(6),
        },
        Parameters = StorageJson.DeserializeParameters(reader.GetString(7)),
        Tags = StorageJson.DeserializeTags(reader.GetString(8)),
        Version = reader.GetInt32(9),
        CreatedUtc = new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(reader.GetInt64(11), TimeSpan.Zero),
        RunCount = reader.GetInt32(12),
        LastRunUtc = reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetInt64(13), TimeSpan.Zero),
        Kind = (PresetKind)reader.GetInt32(14),
    };

    private static string NameKey(string name) => name.Trim().ToLowerInvariant();
}
