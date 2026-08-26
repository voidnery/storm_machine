using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage;

/// <summary>Где лежит база и как себя ведёт хранилище.</summary>
public sealed record StorageOptions
{
    /// <summary>Полный путь к файлу базы. Пусто — путь по умолчанию в профиле пользователя.</summary>
    public string? DatabasePath { get; init; }

    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;

    /// <summary>Применять политику хранения при запуске.</summary>
    public bool ApplyRetentionOnStartup { get; init; } = true;
}

/// <summary>
/// Хранилище прогонов на SQLite.
/// </summary>
/// <remarks>
/// SQLite выбран по итогам исследования (<c>R-14</c>): для настольного продукта его
/// достаточно при двух условиях — обязательной политике хранения и агрегатах, посчитанных
/// при записи. Оба условия выполнены здесь, а не отложены «на когда понадобится».
/// </remarks>
public sealed class SqliteRunStore : IRunStore
{
    private readonly string _connectionString;
    private readonly StorageOptions _options;
    private readonly ILogger<SqliteRunStore>? _logger;

    public SqliteRunStore(StorageOptions options, ILogger<SqliteRunStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;
        Location = options.DatabasePath ?? DefaultDatabasePath();

        var directory = Path.GetDirectoryName(Location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Location,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string Location { get; }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StormMachine",
        "storm.db");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        StorageSchema.EnsureCreated(connection);
        MarkAbandonedRuns(connection);

        if (_options.ApplyRetentionOnStartup)
        {
            var report = ApplyRetention(connection, _options.Retention, dryRun: false);

            if (!report.IsEmpty && _logger?.IsEnabled(LogLevel.Information) == true)
            {
                var deleted = report.RunsDeleted;
                var downsampled = report.RunsDownsampled;
                var samples = report.SamplesDeleted;

                _logger.LogInformation(
                    "Уборка хранилища: удалено прогонов {Runs}, свёрнуто {Downsampled}, удалено сэмплов {Samples}.",
                    deleted, downsampled, samples);
            }
        }
    }

    public async Task<IRunWriter> BeginRunAsync(RunDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();

        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in descriptor.Parameters)
        {
            parameters[key] = value switch
            {
                null => null,
                bool flag => flag ? "true" : "false",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO runs
                    (id, probe_kind, probe_name, shape, target_kind, target_value, target_label,
                     unit, started_ticks, state, context_json, parameters_json)
                VALUES
                    ($id, $kind, $name, $shape, $targetKind, $targetValue, $targetLabel,
                     $unit, $started, $state, $context, $parameters);
                """;

            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$kind", (int)descriptor.Kind);
            command.Parameters.AddWithValue("$name", descriptor.ProbeName);
            command.Parameters.AddWithValue("$shape", (int)descriptor.Shape);
            command.Parameters.AddWithValue("$targetKind", (int)descriptor.Target.Kind);
            command.Parameters.AddWithValue("$targetValue", descriptor.Target.Value);
            command.Parameters.AddWithValue("$targetLabel", (object?)descriptor.Target.Label ?? DBNull.Value);
            command.Parameters.AddWithValue("$unit", (int)descriptor.Unit);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("$state", (int)RunState.Running);
            command.Parameters.AddWithValue("$context", StorageJson.SerializeContext(descriptor.Context));
            command.Parameters.AddWithValue("$parameters", StorageJson.SerializeParameters(parameters));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SqliteRunWriter(connection, id, descriptor.Shape);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.ProbeName))
        {
            filters.Add("probe_name = $probe");
            command.Parameters.AddWithValue("$probe", query.ProbeName);
        }

        if (query.OnlyFailed)
        {
            filters.Add("(success_count = 0 OR success_count < sent_count OR state = 3)");
        }

        if (query.Since is { } since)
        {
            filters.Add("started_ticks >= $since");
            command.Parameters.AddWithValue("$since", since.UtcTicks);
        }

        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

        command.CommandText = $"""
            SELECT id, probe_kind, probe_name, shape, target_value, target_label, resolved_address,
                   started_ticks, completed_ticks, state, sent_count, success_count, median_ms, has_raw_samples
              FROM runs
              {where}
             ORDER BY started_ticks DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));

        var result = new List<RunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSummary(reader));
        }

        return result;
    }

    public async Task<StoredRun?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        RunSummary summary;
        MeasurementContext context;
        MeasurementUnit unit;
        Target target;
        ProbeFact[] facts;
        Dictionary<string, string?> parameters;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, probe_kind, probe_name, shape, target_value, target_label, resolved_address,
                       started_ticks, completed_ticks, state, sent_count, success_count, median_ms,
                       has_raw_samples, target_kind, unit, context_json, parameters_json, facts_json
                  FROM runs
                 WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", id.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            summary = ReadSummary(reader);
            var targetKind = (TargetKind)reader.GetInt32(14);
            unit = (MeasurementUnit)reader.GetInt32(15);
            context = StorageJson.DeserializeContext(reader.GetString(16))
                      ?? throw new InvalidOperationException("Условия измерения не читаются.");
            parameters = StorageJson.DeserializeParameters(reader.GetString(17));
            facts = StorageJson.DeserializeFacts(reader.IsDBNull(18) ? null : reader.GetString(18));

            target = new Target
            {
                Kind = targetKind,
                Value = reader.GetString(4),
                Label = reader.IsDBNull(5) ? null : reader.GetString(5),
            };
        }

        var series = await ReadSeriesAsync(connection, id, cancellationToken).ConfigureAwait(false);
        var samples = await ReadSamplesAsync(connection, id, cancellationToken).ConfigureAwait(false);

        if (series.Count == 0 && samples.Count > 0)
        {
            // Прогон оборвался до подведения итога — агрегаты записать было некому.
            // Сэмплы при этом сохранились, поэтому считаем раскладку сейчас: у оператора
            // не должно быть разницы между «упало» и «нечего смотреть».
            var computed = new List<SeriesStatistics> { SeriesBreakdown.WholeRun(samples) };

            if (summary.Shape != ProbeResultShape.ScalarSeries)
            {
                computed.AddRange(SeriesBreakdown.Compute(summary.Shape, samples));
            }

            series = computed;

            var whole = computed[0].Statistics;
            summary = summary with
            {
                MedianMs = whole.SampleCount == 0 ? null : whole.P50Ms,
            };
        }

        return new StoredRun
        {
            Summary = summary,
            Context = context,
            Unit = unit,
            Target = target,
            Series = series,
            Facts = facts,
            Samples = samples,
            Parameters = parameters,
        };
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<RetentionReport> ApplyRetentionAsync(
        RetentionPolicy policy,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return ApplyRetention(connection, policy, dryRun);
    }

    public async Task<(long SizeBytes, int RunCount, long SampleCount)> GetUsageAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT (SELECT COUNT(*) FROM runs), (SELECT COUNT(*) FROM samples);";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        var runs = reader.GetInt32(0);
        var samples = reader.GetInt64(1);
        var size = File.Exists(Location) ? new FileInfo(Location).Length : 0;

        return (size, runs, samples);
    }

    // ------------------------------------------------------------------ детали

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Внешние ключи в SQLite выключены по умолчанию и включаются на каждое соединение.
        // Без этого каскадное удаление сэмплов при удалении прогона молча не сработает.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Помечает прогоны, оставшиеся открытыми после аварийного завершения.
    /// </summary>
    /// <remarks>
    /// Такой прогон не удаляется: сэмплы, записанные до сбоя, остаются доступными.
    /// Помечается лишь то, что итог не подводился, — иначе оператор увидел бы в журнале
    /// вечно «выполняющийся» прогон и не понял, доверять ему или нет.
    /// </remarks>
    private void MarkAbandonedRuns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
               SET state = $abandoned,
                   sent_count = (SELECT COUNT(*) FROM samples WHERE samples.run_id = runs.id),
                   success_count = (SELECT COUNT(*) FROM samples WHERE samples.run_id = runs.id AND status = 0)
             WHERE state = $running;
            """;

        command.Parameters.AddWithValue("$abandoned", (int)RunState.Abandoned);
        command.Parameters.AddWithValue("$running", (int)RunState.Running);

        var affected = command.ExecuteNonQuery();

        if (affected > 0)
        {
            _logger?.LogWarning(
                "Найдено незавершённых прогонов: {Count}. Помечены как прерванные сбоем; измеренное сохранено.",
                affected);
        }
    }

    private static RetentionReport ApplyRetention(SqliteConnection connection, RetentionPolicy policy, bool dryRun)
    {
        var now = DateTimeOffset.UtcNow;
        var sampleCutoff = now.Subtract(policy.RawSampleHorizon).UtcTicks;
        var runCutoff = now.Subtract(policy.RunHorizon).UtcTicks;

        using var transaction = connection.BeginTransaction();

        int runsToDelete;
        int runsToDownsample;
        long samplesToDelete;

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM runs WHERE started_ticks < $runCutoff),
                    (SELECT COUNT(*) FROM runs WHERE started_ticks < $sampleCutoff AND has_raw_samples = 1),
                    (SELECT COUNT(*) FROM samples
                      WHERE run_id IN (SELECT id FROM runs WHERE started_ticks < $sampleCutoff));
                """;

            count.Parameters.AddWithValue("$runCutoff", runCutoff);
            count.Parameters.AddWithValue("$sampleCutoff", sampleCutoff);

            using var reader = count.ExecuteReader();
            reader.Read();
            runsToDelete = reader.GetInt32(0);
            runsToDownsample = reader.GetInt32(1);
            samplesToDelete = reader.GetInt64(2);
        }

        if (dryRun)
        {
            transaction.Rollback();

            return new RetentionReport
            {
                RunsDeleted = runsToDelete,
                RunsDownsampled = runsToDownsample,
                SamplesDeleted = samplesToDelete,
            };
        }

        using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;

            // Порядок важен: сначала сворачиваем подробности у состарившихся прогонов,
            // затем удаляем совсем старые прогоны целиком. Обратный порядок сделал бы
            // первый шаг частично бессмысленным.
            purge.CommandText = """
                DELETE FROM samples
                 WHERE run_id IN (SELECT id FROM runs WHERE started_ticks < $sampleCutoff);

                UPDATE runs
                   SET has_raw_samples = 0
                 WHERE started_ticks < $sampleCutoff AND has_raw_samples = 1;

                DELETE FROM runs WHERE started_ticks < $runCutoff;
                """;

            purge.Parameters.AddWithValue("$sampleCutoff", sampleCutoff);
            purge.Parameters.AddWithValue("$runCutoff", runCutoff);
            purge.ExecuteNonQuery();
        }

        transaction.Commit();

        return new RetentionReport
        {
            RunsDeleted = runsToDelete,
            RunsDownsampled = runsToDownsample,
            SamplesDeleted = samplesToDelete,
        };
    }

    private static RunSummary ReadSummary(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Kind = (ProbeKind)reader.GetInt32(1),
        ProbeName = reader.GetString(2),
        Shape = (ProbeResultShape)reader.GetInt32(3),
        TargetDisplay = reader.IsDBNull(5) ? reader.GetString(4) : reader.GetString(5),
        ResolvedAddress = reader.IsDBNull(6) ? null : reader.GetString(6),
        StartedUtc = new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
        CompletedUtc = reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero),
        State = (RunState)reader.GetInt32(9),
        SentCount = reader.GetInt32(10),
        SuccessCount = reader.GetInt32(11),
        MedianMs = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        HasRawSamples = reader.GetInt32(13) != 0,
    };

    private static async Task<IReadOnlyList<SeriesStatistics>> ReadSeriesAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT series_key, label, sent_count, success_count,
                   min_ms, max_ms, mean_ms, stddev_ms, p50_ms, p95_ms, p99_ms, jitter_ms
              FROM run_series
             WHERE run_id = $id
             ORDER BY position;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());

        var result = new List<SeriesStatistics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sent = reader.GetInt32(2);
            var success = reader.GetInt32(3);

            var statistics = reader.IsDBNull(4)
                ? LatencyStatistics.Empty
                : new LatencyStatistics
                {
                    SampleCount = success,
                    MinMs = reader.GetDouble(4),
                    MaxMs = reader.GetDouble(5),
                    MeanMs = reader.GetDouble(6),
                    StdDevMs = reader.GetDouble(7),
                    P50Ms = reader.GetDouble(8),
                    P95Ms = reader.GetDouble(9),
                    P99Ms = reader.GetDouble(10),
                    JitterRfc3550Ms = reader.GetDouble(11),
                };

            result.Add(new SeriesStatistics
            {
                Key = reader.GetString(0),
                Label = reader.GetString(1),
                SentCount = sent,
                SuccessCount = success,
                Statistics = statistics,
            });
        }

        return result;
    }

    private static async Task<IReadOnlyList<Sample>> ReadSamplesAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, ts_ticks, value, status, label, grp, responded_by, ttl
              FROM samples
             WHERE run_id = $id
             ORDER BY ordinal;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());

        var result = new List<Sample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Sample
            {
                Sequence = reader.GetInt32(0),
                TimestampUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                Value = reader.IsDBNull(2) ? double.NaN : reader.GetDouble(2),
                Status = (SampleStatus)reader.GetInt32(3),
                Label = reader.IsDBNull(4) ? null : reader.GetString(4),
                Group = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                RespondedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                Ttl = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            });
        }

        return result;
    }
}
