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
    public const int CurrentVersion = 5;

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

        // Обновление идёт по ступеням: база версии 1, созданная прошлым выпуском,
        // должна дойти до текущей, не потеряв данные. Прыжок сразу к последней схеме
        // работал бы только для пустой базы.
        if (version == 0)
        {
            CreateVersion1(connection);
            version = 1;
        }

        if (version == 1)
        {
            UpgradeToVersion2(connection);
            version = 2;
        }

        if (version == 2)
        {
            UpgradeToVersion3(connection);
            version = 3;
        }

        if (version == 3)
        {
            UpgradeToVersion4(connection);
            version = 4;
        }

        if (version == 4)
        {
            UpgradeToVersion5(connection);
            version = 5;
        }

        WriteVersion(connection, version);
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

    /// <summary>Библиотека пресетов и связь прогонов с пресетами.</summary>
    private static void UpgradeToVersion2(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS presets (
                id              TEXT    NOT NULL PRIMARY KEY,
                name            TEXT    NOT NULL,
                name_key        TEXT    NOT NULL,
                description     TEXT,
                probe_name      TEXT    NOT NULL,
                target_kind     INTEGER NOT NULL,
                target_value    TEXT    NOT NULL,
                target_label    TEXT,
                parameters_json TEXT    NOT NULL,
                tags_json       TEXT    NOT NULL,
                version         INTEGER NOT NULL,
                created_ticks   INTEGER NOT NULL,
                updated_ticks   INTEGER NOT NULL,
                run_count       INTEGER NOT NULL DEFAULT 0,
                last_run_ticks  INTEGER
            );
            """);

        // Имя пресета уникально без учёта регистра: две записи «Пинг шлюза» и «пинг шлюза»
        // — это ошибка оператора, а не две разные проверки.
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS ux_presets_name ON presets (name_key);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_presets_probe ON presets (probe_name);");

        // Прогон помнит, каким пресетом и какой его редакцией сделан. Историю редакций
        // хранить не требуется: фактические параметры уже лежат в самом прогоне,
        // и он самодостаточен для истолкования.
        AddColumnIfMissing(connection, "runs", "preset_id", "TEXT");
        AddColumnIfMissing(connection, "runs", "preset_version", "INTEGER");
    }

    /// <summary>
    /// Отметка жизни выполняющегося прогона.
    /// </summary>
    /// <remarks>
    /// Понадобилась в И-7. Пометка брошенных прогонов при старте считала незавершённым
    /// всё, что числится выполняющимся, — и любой второй процесс (консоль рядом
    /// с приложением) объявлял чужое живое измерение прерванным сбоем. С разовыми
    /// пробами по секунде это почти не встречалось; с часовым MTR стало нормой.
    /// <para>
    /// Отметка обновляется при каждом сбросе пачки сэмплов, то есть не реже, чем
    /// приходят измерения. Брошенным считается прогон, чья отметка устарела.
    /// </para>
    /// </remarks>
    private static void UpgradeToVersion3(SqliteConnection connection) =>
        AddColumnIfMissing(connection, "runs", "heartbeat_ticks", "INTEGER");

    /// <summary>
    /// Инвентарь: сканирования, устройства, свидетельства и журнал активных действий.
    /// </summary>
    /// <remarks>
    /// Двух представлений здесь два не по недосмотру, а по смыслу.
    /// <list type="bullet">
    /// <item><c>scan_devices</c> — неизменяемый снимок: что именно было видно в тот раз.
    /// По нему считаются различия между сканированиями, и переписывать его задним числом
    /// нельзя, иначе история перестанет быть историей.</item>
    /// <item><c>devices</c> и <c>device_evidence</c> — сводный инвентарь: всё, что мы
    /// когда-либо узнали об устройстве. Он пересчитывается при каждом сканировании
    /// и именно в нём живёт правка оператора.</item>
    /// </list>
    /// <para>
    /// Свидетельства хранятся строками, а не одним полем JSON: по ним нужно искать
    /// и заменять поштучно — например, чтобы правка оператора перекрыла ровно одно поле,
    /// не тронув остальные.
    /// </para>
    /// </remarks>
    private static void UpgradeToVersion4(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS scans (
                id               TEXT    NOT NULL PRIMARY KEY,
                range_text       TEXT    NOT NULL,
                interface_name   TEXT    NOT NULL,
                started_ticks    INTEGER NOT NULL,
                completed_ticks  INTEGER,
                probed           INTEGER NOT NULL,
                cancelled        INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_scans_started ON scans (started_ticks DESC);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS scan_devices (
                scan_id       TEXT    NOT NULL,
                ordinal       INTEGER NOT NULL,
                address       TEXT    NOT NULL,
                identity      TEXT    NOT NULL,
                is_online     INTEGER NOT NULL,
                evidence_json TEXT    NOT NULL,
                PRIMARY KEY (scan_id, ordinal)
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS devices (
                identity          TEXT    NOT NULL PRIMARY KEY,
                address           TEXT    NOT NULL,
                first_seen_ticks  INTEGER NOT NULL,
                last_seen_ticks   INTEGER NOT NULL,
                is_online         INTEGER NOT NULL DEFAULT 0
            );
            """);

        // Ключ включает значение: одно и то же утверждение от одного источника —
        // это одно свидетельство, у которого обновляется лишь время наблюдения.
        // Разные значения от одного источника — разные свидетельства, и решать
        // между ними должно правило слияния, а не порядок записи.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS device_evidence (
                identity       TEXT    NOT NULL,
                source         INTEGER NOT NULL,
                kind           INTEGER NOT NULL,
                value          TEXT    NOT NULL,
                observed_ticks INTEGER NOT NULL,
                PRIMARY KEY (identity, source, kind, value)
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS audit (
                id        TEXT    NOT NULL PRIMARY KEY,
                at_ticks  INTEGER NOT NULL,
                action    TEXT    NOT NULL,
                target    TEXT    NOT NULL,
                operator_name TEXT NOT NULL,
                details   TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_audit_at ON audit (at_ticks DESC);");
    }

    /// <summary>
    /// Все адреса устройства, а не только последний увиденный.
    /// </summary>
    /// <remarks>
    /// Понадобилось сразу после первого боевого сканирования. Маршрутизаторы,
    /// гипервизоры и хосты с несколькими подсетями занимают несколько адресов одним
    /// интерфейсом; тождество опознаётся по MAC, и такие записи сводились в одно
    /// устройство с одним адресом. Снаружи это выглядело потерей: сканирование
    /// находило 75 адресов, инвентарь перечислял 74 устройства, и разница ничем
    /// не объяснялась.
    /// <para>
    /// Отдельная таблица, а не колонка со списком: по адресам нужно искать —
    /// в том числе чтобы понять, попадает ли устройство в просканированный диапазон.
    /// </para>
    /// </remarks>
    private static void UpgradeToVersion5(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS device_addresses (
                identity        TEXT    NOT NULL,
                address         TEXT    NOT NULL,
                last_seen_ticks INTEGER NOT NULL,
                PRIMARY KEY (identity, address)
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_device_addresses ON device_addresses (address);");

        // Существующие устройства переносят свой единственный адрес: иначе после
        // обновления инвентарь на день остался бы вовсе без адресов.
        Execute(connection, """
            INSERT OR IGNORE INTO device_addresses (identity, address, last_seen_ticks)
            SELECT identity, address, last_seen_ticks FROM devices;
            """);
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        check.Parameters.AddWithValue("$column", column);

        if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
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
