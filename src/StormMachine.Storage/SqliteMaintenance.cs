using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StormMachine.Application.Abstractions;

namespace StormMachine.Storage;

/// <summary>
/// Проверка целостности и лечение файла базы.
/// </summary>
/// <remarks>
/// Первое настоящее повреждение случилось на рабочей базе оператора (И-24): битые
/// страницы в дереве <c>samples</c> и расхождение таблицы <c>runs</c> с её индексом.
/// Ручной разбор показал, что спасается всё, кроме точечно задетого, — эта пересборка
/// повторяет тот разбор как продуктовую возможность.
/// <para>
/// Лечение никогда не правит повреждённый файл на месте: читаемое переносится
/// в новый файл, оригинал целиком уходит в резервную папку рядом с базой. Худший
/// исход лечения — тот же файл в другой папке, а не «стало ещё хуже».
/// </para>
/// </remarks>
public sealed class SqliteMaintenance(IStorageLocation location, ILogger<SqliteMaintenance>? logger = null)
    : IDatabaseMaintenance
{
    private readonly IStorageLocation _location = location ?? throw new ArgumentNullException(nameof(location));

    public string DatabasePath => _location.DatabasePath;

    public async Task<DatabaseHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();

        await using (var connection = new SqliteConnection(ReadOnly(DatabasePath)))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var line = reader.GetString(0);
                    if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(line);
                    }
                }
            }
            catch (SqliteException e)
            {
                // На сильно битой базе сама проверка падает, не успев ничего
                // перечислить, — это тоже диагноз, а не отказ проверки.
                findings.Add(e.Message);
            }
        }

        return new DatabaseHealth
        {
            IsHealthy = findings.Count == 0,
            DatabasePath = DatabasePath,
            Findings = findings,
        };
    }

    public async Task<DatabaseRepairReport> RepairAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = DatabasePath;
        var directory = Path.GetDirectoryName(databasePath)!;

        // Пул держит файл открытым и после закрытия соединений — переносу он помешает.
        SqliteConnection.ClearAllPools();

        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var work = Directory.CreateDirectory(Path.Combine(directory, $"repair-{stamp}")).FullName;
        var sourceCopy = Path.Combine(work, Path.GetFileName(databasePath));
        var rebuilt = Path.Combine(work, "rebuilt.db");

        try
        {
            // Рабочая копия со всеми спутниками: открытие копии накатит WAL,
            // не тронув оригинал, — измерения последнего сеанса не теряются.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = databasePath + suffix;
                if (File.Exists(file))
                {
                    File.Copy(file, sourceCopy + suffix, overwrite: true);
                }
            }

            DatabaseRepairReport report;
            await using (var source = new SqliteConnection(ReadWrite(sourceCopy)))
            await using (var target = new SqliteConnection(ReadWrite(rebuilt)))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await target.OpenAsync(cancellationToken).ConfigureAwait(false);

                report = Rebuild(source, target);

                // Пересобранное обязано быть целым — иначе лечение не состоялось
                // и подменять рабочий файл нечем.
                using var verify = target.CreateCommand();
                verify.CommandText = "PRAGMA integrity_check;";
                var verdict = (string?)verify.ExecuteScalar();
                if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Пересборка не дала целой базы ({verdict}) — файл не подменён, оригинал не тронут.");
                }
            }

            SqliteConnection.ClearAllPools();

            // Подмена: оригинал — в резервную папку, новый файл — на его место.
            var backup = Directory.CreateDirectory(Path.Combine(directory, $"corrupt-{stamp}")).FullName;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = databasePath + suffix;
                if (File.Exists(file))
                {
                    File.Move(file, Path.Combine(backup, Path.GetFileName(file)));
                }
            }

            File.Move(rebuilt, databasePath);

            logger?.LogWarning(
                "База пересобрана. Повреждённый файл: {Backup}. Прогонов {Runs}, сэмплов {Samples}; "
                + "без сырых сэмплов {WithoutSamples}, потеряно целиком {Lost}.",
                backup, report.RunsKept, report.SamplesKept, report.RunsWithoutSamples, report.RunsLost);

            return report with { BackupPath = backup };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Не удалившаяся рабочая папка — мусор, а не причина объявить
                // состоявшееся лечение неудавшимся.
            }
        }
    }

    /// <summary>
    /// Переносит из повреждённой базы в новую всё, что читается.
    /// </summary>
    /// <remarks>
    /// Сэмплы переносятся по прогонам: повреждение локализовано в страницах, и отказ
    /// чтения одного прогона не повод бросать остальные. У прогона, оставшегося без
    /// сырья, снимается флаг <c>has_raw_samples</c> — продукт покажет его как прогон
    /// со свёрнутыми сэмплами, и это правда. Агрегаты, чьи прогоны потеряны целиком,
    /// удаляются: до них не добраться никаким экраном, а нарушенная ссылка ломала бы
    /// каскадное удаление.
    /// </remarks>
    private static DatabaseRepairReport Rebuild(SqliteConnection source, SqliteConnection target)
    {
        Execute(target, "PRAGMA foreign_keys = OFF;");

        var schema = new List<(string Type, string Name, string Sql)>();
        using (var command = source.CreateCommand())
        {
            command.CommandText =
                "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                schema.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (type, _, sql) in schema.Where(s => s.Type == "table"))
        {
            Execute(target, sql);
        }

        var partial = new List<string>();
        long samplesKept = 0;

        foreach (var (_, name, _) in schema.Where(s => s.Type == "table" && s.Name != "samples"))
        {
            var copied = CopyTable(source, target, name, out var failure);
            if (failure is not null)
            {
                partial.Add($"{name}: {copied}");
            }
        }

        var runIds = new List<string>();
        using (var command = target.CreateCommand())
        {
            command.CommandText = "SELECT id FROM runs ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runIds.Add(reader.GetString(0));
            }
        }

        var runsWithoutSamples = 0;
        foreach (var runId in runIds)
        {
            var copied = CopyTable(
                source, target, "samples", out var failure,
                "SELECT * FROM samples WHERE run_id = $run ORDER BY ordinal;",
                ("$run", runId));

            if (failure is null)
            {
                samplesKept += copied;
            }
            else
            {
                // Часть сэмплов могла успеть перенестись до битой страницы — убрать:
                // усечённый ряд выглядит как короткий прогон, а это неправда.
                Execute(target, "DELETE FROM samples WHERE run_id = $run;", ("$run", runId));
                Execute(target, "UPDATE runs SET has_raw_samples = 0 WHERE id = $run;", ("$run", runId));
                runsWithoutSamples++;
            }
        }

        // Индексы и триггеры — после данных: они перестраиваются с таблиц,
        // и расхождение таблицы с повреждённым индексом здесь исчезает.
        foreach (var (_, _, sql) in schema.Where(s => s.Type != "table"))
        {
            Execute(target, sql);
        }

        var runsLost = (long)Scalar(
            target, "SELECT COUNT(DISTINCT run_id) FROM run_series WHERE run_id NOT IN (SELECT id FROM runs);");
        Execute(target, "DELETE FROM run_series WHERE run_id NOT IN (SELECT id FROM runs);");

        return new DatabaseRepairReport
        {
            BackupPath = string.Empty,
            RunsKept = runIds.Count,
            SamplesKept = samplesKept,
            RunsWithoutSamples = runsWithoutSamples,
            RunsLost = (int)runsLost,
            PartialTables = partial,
        };
    }

    /// <summary>Переносит строки одного запроса; возвращает, сколько удалось до отказа.</summary>
    private static long CopyTable(
        SqliteConnection source,
        SqliteConnection target,
        string table,
        out string? failure,
        string? select = null,
        params (string Name, object Value)[] parameters)
    {
        failure = null;
        long copied = 0;

        using var read = source.CreateCommand();
        read.CommandText = select ?? $"SELECT * FROM \"{table}\";";
        foreach (var (name, value) in parameters)
        {
            read.Parameters.AddWithValue(name, value);
        }

        SqliteDataReader reader;
        try
        {
            reader = read.ExecuteReader();
        }
        catch (SqliteException e)
        {
            failure = e.Message;
            return 0;
        }

        using (reader)
        {
            using var insert = target.CreateCommand();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            insert.CommandText =
                $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) "
                + $"VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))});";

            var arguments = columns.Select((_, i) => insert.Parameters.AddWithValue($"$p{i}", DBNull.Value)).ToArray();

            using var transaction = target.BeginTransaction();
            insert.Transaction = transaction;

            try
            {
                while (reader.Read())
                {
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        arguments[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    }

                    insert.ExecuteNonQuery();
                    copied++;
                }
            }
            catch (SqliteException e)
            {
                // Дочитались до битой страницы. Прочитанное до неё — настоящее,
                // и откатывать его вместе с отказом значило бы терять лишнее.
                failure = e.Message;
            }

            transaction.Commit();
        }

        return copied;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private static string ReadOnly(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    private static string ReadWrite(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();
}
