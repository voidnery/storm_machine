using Microsoft.Data.Sqlite;

namespace StormMachine.Storage;

/// <summary>
/// Схема базы и её обновление.
/// </summary>
/// <remarks>
/// Версия схемы хранится в самой базе. Продукт настольный: у пользователя может лежать
/// файл, созданный версией годичной давности, и открыть его надо без потери данных.
/// </remarks>
internal static class StorageSchema
{
    /// <summary>Текущая версия схемы. Растёт при каждом изменении структуры.</summary>
    public const int CurrentVersion = 1;

    public static void EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");

        // NORMAL вместо FULL: при WAL это безопасно для целостности базы и заметно
        // дешевле при записи сэмплов пачками. Потеря последней пачки при внезапном
        // отключении питания допустима — это измерения, а не деньги.
        Execute(connection, "PRAGMA synchronous = NORMAL;");

        var version = ReadVersion(connection);

        if (version == CurrentVersion)
        {
            return;
        }

        if (version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"База создана более новой версией продукта (схема {version}, поддерживается {CurrentVersion}). "
                + "Обнови Storm Machine или укажи другой файл базы.");
        }

        if (version == 0)
        {
            CreateVersion1(connection);
        }

        WriteVersion(connection, CurrentVersion);
    }

    private static void CreateVersion1(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS runs (
                id                TEXT    NOT NULL PRIMARY KEY,
                probe_kind        INTEGER NOT NULL,
                probe_name        TEXT    NOT NULL,
                shape             INTEGER NOT NULL,
                target_kind       INTEGER NOT NULL,
                target_value      TEXT    NOT NULL,
                target_label      TEXT,
                resolved_address  TEXT,
                unit              INTEGER NOT NULL,
                started_ticks     INTEGER NOT NULL,
                completed_ticks   INTEGER,
                state             INTEGER NOT NULL,
                sent_count        INTEGER NOT NULL DEFAULT 0,
                success_count     INTEGER NOT NULL DEFAULT 0,
                median_ms         REAL,
                has_raw_samples   INTEGER NOT NULL DEFAULT 1,
                context_json      TEXT    NOT NULL,
                parameters_json   TEXT    NOT NULL,
                facts_json        TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_runs_started ON runs (started_ticks DESC);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_runs_probe ON runs (probe_name, started_ticks DESC);");

        // Ключ (run_id, ordinal), а НЕ (run_id, seq).
        // У фазовых проб порядковый номер повторяется: пять фаз одного запроса HTTP
        // несут один и тот же номер попытки. Порядок поступления — единственное,
        // что уникально во всех четырёх формах результата.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS samples (
                run_id        TEXT    NOT NULL,
                ordinal       INTEGER NOT NULL,
                seq           INTEGER NOT NULL,
                ts_ticks      INTEGER NOT NULL,
                value         REAL,
                status        INTEGER NOT NULL,
                label         TEXT,
                grp           INTEGER,
                responded_by  TEXT,
                ttl           INTEGER,
                PRIMARY KEY (run_id, ordinal),
                FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
            ) WITHOUT ROWID;
            """);

        // Агрегаты по рядам. Считаются один раз при записи и переживают удаление
        // сырых сэмплов политикой хранения — иначе история и отчёты за год
        // остались бы без данных.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS run_series (
                run_id        TEXT    NOT NULL,
                series_key    TEXT    NOT NULL,
                label         TEXT    NOT NULL,
                position      INTEGER NOT NULL,
                sent_count    INTEGER NOT NULL,
                success_count INTEGER NOT NULL,
                min_ms        REAL,
                max_ms        REAL,
                mean_ms       REAL,
                stddev_ms     REAL,
                p50_ms        REAL,
                p95_ms        REAL,
                p99_ms        REAL,
                jitter_ms     REAL,
                PRIMARY KEY (run_id, series_key),
                FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
            ) WITHOUT ROWID;
            """);

        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
    }

    public static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";

        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteVersion(SqliteConnection connection, int version)
    {
        Execute(connection, "DELETE FROM schema_version;");

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO schema_version (version) VALUES ($version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
