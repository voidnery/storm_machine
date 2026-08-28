using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage;

/// <summary>
/// Эталоны в той же базе, что и всё остальное.
/// </summary>
/// <remarks>
/// Условия измерения хранятся вместе с числами, а не собираются заново из прогона:
/// прогон удалит политика хранения, а эталон обязан пережить исходное измерение —
/// его и заводят ради того, чтобы сравнивать с ним годами.
/// </remarks>
public sealed class SqliteBaselineStore(SqliteRunStore runStore) : IBaselineStore
{
    private const string Columns =
        "id, name, description, subject, target_kind, target_value, target_label, "
        + "unit, context_json, metrics_json, run_id, captured_ticks";

    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public async Task<IReadOnlyList<Baseline>> ListAsync(
        BaselineQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            filters.Add("subject = $subject");
            command.Parameters.AddWithValue("$subject", query.Subject);
        }

        var where = filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty;

        command.CommandText =
            $"SELECT {Columns} FROM baselines{where} ORDER BY captured_ticks DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));

        var found = new List<Baseline>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Read(reader));
        }

        // Поиск по имени и описанию делается в памяти, а не в SQL: функция LOWER()
        // в SQLite приводит к нижнему регистру только латиницу, и «Офис» никогда
        // не совпал бы с «офис». Эталонов десятки, разница незаметна.
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return found;
        }

        var needle = query.Search.Trim();

        return
        [
            .. found.Where(b =>
                b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (b.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)),
        ];
    }

    public async Task<Baseline?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM baselines WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    /// <remarks>
    /// Точное имя выигрывает у совпадения по началу: иначе эталон, названный ровно так,
    /// стало бы невозможно открыть после появления соседа с более длинным именем.
    /// Неоднозначное сокращение — ошибка, а не догадка.
    /// </remarks>
    public async Task<Baseline?> FindAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        var needle = nameOrId.Trim();
        var all = await ListAsync(new BaselineQuery { Limit = 10_000 }, cancellationToken).ConfigureAwait(false);

        var exact = all.FirstOrDefault(b => string.Equals(b.Name, needle, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var matches = all
            .Where(b => b.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                        || b.Id.ToString().StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"«{nameOrId}» подходит сразу нескольким эталонам: "
                + string.Join(", ", matches.Select(m => m.Name))
                + ". Уточни имя."),
        };
    }

    public async Task SaveAsync(Baseline baseline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO baselines (id, name, description, subject, target_kind, target_value,
                                   target_label, unit, context_json, metrics_json, run_id, captured_ticks)
            VALUES ($id, $name, $description, $subject, $targetKind, $targetValue,
                    $targetLabel, $unit, $context, $metrics, $run, $captured)
            ON CONFLICT(id) DO UPDATE SET
                name           = $name,
                description    = $description,
                subject        = $subject,
                target_kind    = $targetKind,
                target_value   = $targetValue,
                target_label   = $targetLabel,
                unit           = $unit,
                context_json   = $context,
                metrics_json   = $metrics,
                run_id         = $run,
                captured_ticks = $captured;
            """;

        command.Parameters.AddWithValue("$id", baseline.Id.ToString());
        command.Parameters.AddWithValue("$name", baseline.Name);
        command.Parameters.AddWithValue("$description", (object?)baseline.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", baseline.Subject);
        command.Parameters.AddWithValue("$targetKind", (int)baseline.Target.Kind);
        command.Parameters.AddWithValue("$targetValue", baseline.Target.Value);
        command.Parameters.AddWithValue("$targetLabel", (object?)baseline.Target.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", (int)baseline.Unit);
        command.Parameters.AddWithValue("$context", StorageJson.SerializeContext(baseline.Context));
        command.Parameters.AddWithValue("$metrics", StorageJson.SerializeBaselineMetrics([.. baseline.Metrics]));
        command.Parameters.AddWithValue("$run", (object?)baseline.RunId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$captured", baseline.CapturedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM baselines WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static Baseline Read(SqliteDataReader reader) => new()
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
        Unit = (MeasurementUnit)reader.GetInt32(7),

        // Условия — единственное, без чего эталон бессмыслен. Строка, у которой
        // их не разобрать, испорчена, и подставлять сюда пустые условия нельзя:
        // сравнение молча потеряло бы проверку сопоставимости.
        Context = StorageJson.DeserializeContext(reader.GetString(8))
                  ?? throw new InvalidOperationException(
                      $"У эталона «{reader.GetString(1)}» не читаются условия измерения."),

        Metrics = StorageJson.DeserializeBaselineMetrics(reader.GetString(9)),
        RunId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
        CapturedUtc = new DateTimeOffset(reader.GetInt64(11), TimeSpan.Zero),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
