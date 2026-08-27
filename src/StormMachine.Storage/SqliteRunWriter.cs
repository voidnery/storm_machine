using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Storage;

/// <summary>
/// Запись одного прогона в SQLite.
/// </summary>
/// <remarks>
/// Сэмплы копятся в памяти и сбрасываются пачками: запись каждого по отдельности
/// в собственной транзакции превратила бы измерение с интервалом 10 мс в измерение
/// скорости диска.
/// <para>
/// Пачка ограничена и по размеру, и по времени. Только по размеру было бы недостаточно:
/// монитор с интервалом в минуту накапливал бы пачку часами, и всё это время
/// незаписанные данные жили бы только в памяти.
/// </para>
/// </remarks>
internal sealed class SqliteRunWriter : IRunWriter
{
    private const int BatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly SqliteConnection _connection;
    private readonly ProbeResultShape _shape;
    private readonly List<Sample> _pending = new(BatchSize);
    private readonly List<Sample> _all = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _ordinal;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private bool _completed;
    private bool _disposed;

    public SqliteRunWriter(SqliteConnection connection, Guid runId, ProbeResultShape shape)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _shape = shape;
        RunId = runId;
    }

    public Guid RunId { get; }

    public async ValueTask AppendAsync(Sample sample, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _pending.Add(sample);
            _all.Add(sample);

            var due = _pending.Count >= BatchSize
                      || DateTimeOffset.UtcNow - _lastFlush >= FlushInterval;

            if (due)
            {
                FlushPending();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(
        IReadOnlyList<ProbeFact> facts,
        string? resolvedAddress,
        bool wasCancelled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_completed)
            {
                return;
            }

            FlushPending();
            WriteSeries();
            WriteSummary(facts, resolvedAddress, wasCancelled);
            _completed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Досбрасываем даже без подведения итога: процесс мог закрываться аварийно,
            // и уже измеренное терять нельзя. Прогон останется в состоянии «открыт»
            // и будет помечен при следующем запуске.
            FlushPending();
        }
        catch (SqliteException)
        {
            // Хранилище могло быть уже закрыто — терять нечего, кроме последней пачки.
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO samples (run_id, ordinal, seq, ts_ticks, value, status, label, grp, responded_by, ttl)
            VALUES ($run, $ordinal, $seq, $ts, $value, $status, $label, $grp, $by, $ttl);
            """;

        var runParameter = command.Parameters.Add("$run", SqliteType.Text);
        var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var seqParameter = command.Parameters.Add("$seq", SqliteType.Integer);
        var tsParameter = command.Parameters.Add("$ts", SqliteType.Integer);
        var valueParameter = command.Parameters.Add("$value", SqliteType.Real);
        var statusParameter = command.Parameters.Add("$status", SqliteType.Integer);
        var labelParameter = command.Parameters.Add("$label", SqliteType.Text);
        var groupParameter = command.Parameters.Add("$grp", SqliteType.Integer);
        var byParameter = command.Parameters.Add("$by", SqliteType.Text);
        var ttlParameter = command.Parameters.Add("$ttl", SqliteType.Integer);

        runParameter.Value = RunId.ToString();

        foreach (var sample in _pending)
        {
            ordinalParameter.Value = _ordinal++;
            seqParameter.Value = sample.Sequence;
            tsParameter.Value = sample.TimestampUtc.UtcTicks;

            // NaN в SQLite превращается в NULL при чтении, поэтому пишем NULL сразу:
            // неуспешный сэмпл не должен выглядеть как измеренный ноль.
            valueParameter.Value = double.IsNaN(sample.Value) ? DBNull.Value : sample.Value;
            statusParameter.Value = (int)sample.Status;
            labelParameter.Value = (object?)sample.Label ?? DBNull.Value;
            groupParameter.Value = sample.Group.HasValue ? sample.Group.Value : DBNull.Value;
            byParameter.Value = (object?)sample.RespondedBy ?? DBNull.Value;
            ttlParameter.Value = sample.Ttl.HasValue ? sample.Ttl.Value : DBNull.Value;

            command.ExecuteNonQuery();
        }

        // Отметка жизни в той же транзакции, что и сэмплы: пока прогон пишет, он жив,
        // и никакой другой процесс не должен считать его прерванным сбоем.
        using (var heartbeat = _connection.CreateCommand())
        {
            heartbeat.Transaction = transaction;
            heartbeat.CommandText = "UPDATE runs SET heartbeat_ticks = $now WHERE id = $run;";
            heartbeat.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            heartbeat.Parameters.AddWithValue("$run", RunId.ToString());
            heartbeat.ExecuteNonQuery();
        }

        transaction.Commit();

        _pending.Clear();
        _lastFlush = DateTimeOffset.UtcNow;
    }

    private void WriteSeries()
    {
        var series = new List<SeriesStatistics>(SeriesBreakdown.Compute(_shape, _all));

        // Агрегат по всему прогону нужен всегда: список прогонов показывает одну цифру
        // на строку и не разворачивает раскладку.
        if (_shape != ProbeResultShape.ScalarSeries)
        {
            series.Insert(0, SeriesBreakdown.WholeRun(_all));
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO run_series
                (run_id, series_key, label, position, sent_count, success_count,
                 min_ms, max_ms, mean_ms, stddev_ms, p50_ms, p95_ms, p99_ms, jitter_ms)
            VALUES ($run, $key, $label, $position, $sent, $success,
                    $min, $max, $mean, $stddev, $p50, $p95, $p99, $jitter);
            """;

        for (var position = 0; position < series.Count; position++)
        {
            var item = series[position];
            var stats = item.Statistics;
            var empty = stats.SampleCount == 0;

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$run", RunId.ToString());
            command.Parameters.AddWithValue("$key", item.Key);
            command.Parameters.AddWithValue("$label", item.Label);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$sent", item.SentCount);
            command.Parameters.AddWithValue("$success", item.SuccessCount);
            command.Parameters.AddWithValue("$min", empty ? DBNull.Value : stats.MinMs);
            command.Parameters.AddWithValue("$max", empty ? DBNull.Value : stats.MaxMs);
            command.Parameters.AddWithValue("$mean", empty ? DBNull.Value : stats.MeanMs);
            command.Parameters.AddWithValue("$stddev", empty ? DBNull.Value : stats.StdDevMs);
            command.Parameters.AddWithValue("$p50", empty ? DBNull.Value : stats.P50Ms);
            command.Parameters.AddWithValue("$p95", empty ? DBNull.Value : stats.P95Ms);
            command.Parameters.AddWithValue("$p99", empty ? DBNull.Value : stats.P99Ms);
            command.Parameters.AddWithValue("$jitter", empty ? DBNull.Value : stats.JitterRfc3550Ms);

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void WriteSummary(IReadOnlyList<ProbeFact> facts, string? resolvedAddress, bool wasCancelled)
    {
        var whole = SeriesBreakdown.WholeRun(_all);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
               SET completed_ticks = $completed,
                   state           = $state,
                   sent_count      = $sent,
                   success_count   = $success,
                   median_ms       = $median,
                   resolved_address = COALESCE($resolved, resolved_address),
                   facts_json      = $facts
             WHERE id = $run;
            """;

        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$state", (int)(wasCancelled ? RunState.Cancelled : RunState.Completed));
        command.Parameters.AddWithValue("$sent", whole.SentCount);
        command.Parameters.AddWithValue("$success", whole.SuccessCount);
        command.Parameters.AddWithValue(
            "$median",
            whole.Statistics.SampleCount == 0 ? DBNull.Value : whole.Statistics.P50Ms);
        command.Parameters.AddWithValue("$resolved", (object?)resolvedAddress ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$facts",
            facts.Count == 0 ? DBNull.Value : StorageJson.SerializeFacts([.. facts]));
        command.Parameters.AddWithValue("$run", RunId.ToString());

        command.ExecuteNonQuery();
    }
}
