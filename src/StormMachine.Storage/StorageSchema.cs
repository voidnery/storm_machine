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
    public const int CurrentVersion = 13;

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
        //
        // Все ступени и отметка версии выполняются ОДНОЙ транзакцией. Без неё смерть
        // процесса посреди обновления оставляет базу в состоянии, из которого она
        // больше не открывается: схема частично новая, отметка версии старая, и при
        // следующем запуске ступень падает на «duplicate column name». В SQLite
        // изменение схемы транзакционно, и пользоваться этим — не роскошь, а условие
        // того, чтобы обновление продукта не стоило человеку истории измерений.
        using var upgrade = connection.BeginTransaction();

        version = Upgrade(connection, version);

        WriteVersion(connection, version);
        upgrade.Commit();
    }

    /// <summary>Выполняет ступени обновления и возвращает достигнутую версию.</summary>
    private static int Upgrade(SqliteConnection connection, int version)
    {
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

        if (version == 5)
        {
            UpgradeToVersion6(connection);
            version = 6;
        }

        if (version == 6)
        {
            UpgradeToVersion7(connection);
            version = 7;
        }

        if (version == 7)
        {
            UpgradeToVersion8(connection);
            version = 8;
        }

        if (version == 8)
        {
            UpgradeToVersion9(connection);
            version = 9;
        }

        if (version == 9)
        {
            UpgradeToVersion10(connection);
            version = 10;
        }

        if (version == 10)
        {
            UpgradeToVersion11(connection);
            version = 11;
        }

        if (version == 11)
        {
            UpgradeToVersion12(connection);
            version = 12;
        }

        if (version == 12)
        {
            UpgradeToVersion13(connection);
            version = 13;
        }

        return version;
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

    /// <summary>
    /// Правки оператора: объединённые дубли и связи, нарисованные вручную.
    /// </summary>
    /// <remarks>
    /// Хранится не результат правки, а сама правка. Разница принципиальная: инвентарь
    /// и карта пересчитываются из свидетельств при каждом сканировании, и правка,
    /// записанная в результат, была бы затёрта первым же пересчётом. Записанная
    /// отдельно — переживает любое их число, а отменяется удалением одной строки.
    /// </remarks>
    /// <summary>
    /// Сопряжённые агенты.
    /// </summary>
    /// <remarks>
    /// Ключ — отпечаток, а не адрес. Имя машины и адрес меняются: DHCP выдал другой,
    /// машину переименовали, площадка переехала. Отпечаток не меняется, он и есть
    /// личность агента, и агент, сменивший адрес, обязан остаться тем же агентом.
    /// </remarks>
    /// <summary>
    /// История наблюдений за оборудованием: счётчики портов и услышанное в эфире.
    /// </summary>
    /// <remarks>
    /// Появилась в И-21. До неё оба вида данных продукт читать умел, а хранить — нет:
    /// загрузка порта мерилась на месте и показывалась, услышанные соседи и серверы DHCP
    /// показывались и забывались. Без истории нет ответа ни на «что было с портом ночью»,
    /// ни на «когда появился этот сервер DHCP» — а это первые вопросы, которые задают,
    /// увидев неладное.
    /// <para>
    /// Обе таблицы — временные ряды и растут линейно, поэтому попадают под ту же политику
    /// хранения, что и сэмплы измерений. Иначе они превратили бы файл базы в проблему
    /// ровно тем же способом, от которого политика и защищает.
    /// </para>
    /// </remarks>
    private static void UpgradeToVersion13(SqliteConnection connection)
    {
        // Счётчики порта. Ключ включает момент: это ряд, а не текущее состояние.
        //
        // Значения счётчиков не хранятся сырыми — хранится уже посчитанная разница
        // двух снимков. Сырой счётчик 32 бит переполняется на гигабитном порту
        // за полминуты, и ряд таких значений без пометок о переполнении бесполезен;
        // разница же считается в момент опроса, когда оба снимка на руках.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS port_load (
                device        TEXT    NOT NULL,
                if_index      INTEGER NOT NULL,
                at_ticks      INTEGER NOT NULL,
                if_name       TEXT,
                interval_ms   INTEGER NOT NULL,
                in_bps        REAL    NOT NULL,
                out_bps       REAL    NOT NULL,
                speed_bps     INTEGER NOT NULL,
                in_errors     INTEGER NOT NULL,
                out_errors    INTEGER NOT NULL,
                in_discards   INTEGER NOT NULL,
                out_discards  INTEGER NOT NULL,
                PRIMARY KEY (device, if_index, at_ticks)
            ) WITHOUT ROWID;
            """);

        // Выборка «что было с этим портом за сутки» идёт по ведущим колонкам ключа,
        // а «что было со всем оборудованием за час» — по времени, и для второй нужен
        // свой индекс.
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_port_load_at ON port_load (at_ticks DESC);");

        // Услышанные соседи. Ключ — кто и через какой наш порт: одно и то же
        // соседство, услышанное десять раз, это одно соседство с обновлённым
        // временем, а не десять записей.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS heard_neighbors (
                local_if      TEXT    NOT NULL,
                chassis       TEXT    NOT NULL,
                port_id       TEXT    NOT NULL,
                system_name   TEXT,
                port_name     TEXT,
                protocol      INTEGER NOT NULL,
                first_seen    INTEGER NOT NULL,
                last_seen     INTEGER NOT NULL,
                PRIMARY KEY (local_if, chassis, port_id)
            ) WITHOUT ROWID;
            """);

        Execute(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_heard_neighbors_seen ON heard_neighbors (last_seen DESC);");

        // Серверы DHCP. Здесь ключ включает то, что сервер раздаёт: сервер, начавший
        // объявлять другой шлюз, — это событие, ради которого захват и слушают,
        // и потерять его, обновив строку на месте, нельзя.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS heard_dhcp (
                server        TEXT    NOT NULL,
                offered_gw    TEXT    NOT NULL,
                server_mac    TEXT,
                offered_dns   TEXT    NOT NULL,
                first_seen    INTEGER NOT NULL,
                last_seen     INTEGER NOT NULL,
                sightings     INTEGER NOT NULL,
                PRIMARY KEY (server, offered_gw)
            ) WITHOUT ROWID;
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_heard_dhcp_seen ON heard_dhcp (last_seen DESC);");
    }

    private static void UpgradeToVersion12(SqliteConnection connection)
    {
        // Учётные данные SNMP. Пароли лежат зашифрованными средствами машины —
        // шифрует их хранилище, а не эта таблица; здесь важно другое: они в отдельных
        // колонках, а не в общем JSON, чтобы случайная выгрузка таблицы в поддержку
        // не вынесла их вместе со всем остальным.
        //
        // Порядок перебора — своя колонка: на объекте, где ядро отвечает по v3,
        // а доступ по v2c, важно, какой набор пробуется первым.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS snmp_credentials (
                id                TEXT    NOT NULL PRIMARY KEY,
                name              TEXT    NOT NULL UNIQUE,
                version           INTEGER NOT NULL,
                community         TEXT,
                user_name         TEXT,
                auth_protocol     INTEGER NOT NULL DEFAULT 0,
                auth_password     TEXT,
                privacy_protocol  INTEGER NOT NULL DEFAULT 0,
                privacy_password  TEXT,
                port              INTEGER NOT NULL DEFAULT 161,
                timeout_ms        INTEGER NOT NULL DEFAULT 3000,
                retries           INTEGER NOT NULL DEFAULT 1,
                sort_order        INTEGER NOT NULL DEFAULT 0,
                created_ticks     INTEGER NOT NULL,
                updated_ticks     INTEGER NOT NULL
            );
            """);

        Execute(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_snmp_credentials_order ON snmp_credentials (sort_order, name);");
    }

    private static void UpgradeToVersion11(SqliteConnection connection)
    {
        // Профили сетевого окружения. Активным может быть только один — это
        // обеспечивается частичным уникальным индексом, а не договорённостью
        // в коде: два активных профиля означали бы два набора порогов на одно
        // измерение, и поймать такое потом было бы нечем.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS profiles (
                id             TEXT    NOT NULL PRIMARY KEY,
                name           TEXT    NOT NULL UNIQUE,
                description    TEXT,
                targets_json   TEXT    NOT NULL DEFAULT '[]',
                thresholds_json TEXT   NOT NULL DEFAULT '[]',
                monitors_json  TEXT    NOT NULL DEFAULT '[]',
                signature_json TEXT    NOT NULL DEFAULT '{}',
                is_active      INTEGER NOT NULL DEFAULT 0,
                created_ticks  INTEGER NOT NULL,
                updated_ticks  INTEGER NOT NULL
            );
            """);

        Execute(
            connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_profiles_active ON profiles (is_active) WHERE is_active = 1;");
    }

    private static void UpgradeToVersion10(SqliteConnection connection)
    {
        // Эталоны. Условия измерения хранятся вместе с числами и не выносятся
        // в колонки: без них эталон превращается в набор цифр неизвестного
        // происхождения, а сравнение с ним — в красивую ошибку.
        //
        // Ссылка на прогон намеренно без внешнего ключа: политика хранения удаляет
        // старые прогоны, а эталон обязан пережить исходное измерение — он и заводится
        // ради того, чтобы сравнивать с ним годами.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS baselines (
                id             TEXT    NOT NULL PRIMARY KEY,
                name           TEXT    NOT NULL UNIQUE,
                description    TEXT,
                subject        TEXT    NOT NULL,
                target_kind    INTEGER NOT NULL,
                target_value   TEXT    NOT NULL,
                target_label   TEXT,
                unit           INTEGER NOT NULL,
                context_json   TEXT    NOT NULL,
                metrics_json   TEXT    NOT NULL,
                run_id         TEXT,
                captured_ticks INTEGER NOT NULL
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_baselines_subject ON baselines (subject);");
    }

    private static void UpgradeToVersion9(SqliteConnection connection)
    {
        // Пресет научился хранить сценарий, а не только пробу. Колонка probe_name
        // при этом сохранила имя: переименовывать её значило бы переписывать таблицу
        // ради косметики, а в файлах обмена поле всё равно осталось прежним —
        // наборы пресетов уже разошлись по рукам.
        AddColumnIfMissing(connection, "presets", "kind", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void UpgradeToVersion8(SqliteConnection connection)
    {
        // Определение монитора и его текущее состояние в одной строке. Разносить их
        // по двум таблицам смысла нет: состояние ровно одно на монитор и живёт
        // ровно столько же.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS monitors (
                id               TEXT    NOT NULL PRIMARY KEY,
                name             TEXT    NOT NULL UNIQUE,
                description      TEXT,
                kind             INTEGER NOT NULL,
                subject          TEXT    NOT NULL,
                target_kind      INTEGER NOT NULL,
                target_value     TEXT    NOT NULL,
                target_label     TEXT,
                parameters_json  TEXT    NOT NULL DEFAULT '{}',
                thresholds_json  TEXT    NOT NULL DEFAULT '[]',
                schedule_json    TEXT    NOT NULL,
                alert_json       TEXT,
                objective_json   TEXT,
                preset_id        TEXT,
                enabled          INTEGER NOT NULL DEFAULT 1,
                created_ticks    INTEGER NOT NULL,
                updated_ticks    INTEGER NOT NULL,
                next_due_ticks   INTEGER,
                state_level      INTEGER NOT NULL DEFAULT 0,
                last_run_ticks   INTEGER,
                last_summary     TEXT,
                alert_state_json TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_monitors_due ON monitors (next_due_ticks);");

        // Журнал проверок — источник всей доступности. Хранит и то, чего не измеряли:
        // пропуски и обслуживание. Без них доступность считалась бы по одним удачам.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS monitor_checks (
                id             TEXT    NOT NULL PRIMARY KEY,
                monitor_id     TEXT    NOT NULL,
                started_ticks  INTEGER NOT NULL,
                duration_ticks INTEGER NOT NULL DEFAULT 0,
                kind           INTEGER NOT NULL,
                level          INTEGER NOT NULL,
                summary        TEXT    NOT NULL,
                run_id         TEXT,
                metric         TEXT,
                value          REAL,
                threshold      REAL,
                missed_count   INTEGER NOT NULL DEFAULT 0,
                error          TEXT,
                FOREIGN KEY (monitor_id) REFERENCES monitors (id) ON DELETE CASCADE
            );
            """);

        Execute(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_checks_monitor ON monitor_checks (monitor_id, started_ticks DESC);");

        // Лента алертов внешнего ключа НЕ имеет — намеренно. Удаление монитора убирает
        // его проверки: без монитора они не значат ничего. Но факт «в четверг в три ночи
        // сработал алерт» остаётся фактом и после того, как монитор убрали, — тем более
        // что убрать его могли именно поэтому. Имя монитора продублировано в строке,
        // чтобы событие читалось и без него.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS alerts (
                id            TEXT    NOT NULL PRIMARY KEY,
                monitor_id    TEXT    NOT NULL,
                monitor_name  TEXT    NOT NULL,
                at_ticks      INTEGER NOT NULL,
                action        INTEGER NOT NULL,
                level         INTEGER NOT NULL,
                reason        TEXT    NOT NULL,
                summary       TEXT,
                check_id      TEXT,
                notified      INTEGER NOT NULL DEFAULT 0,
                channels_json TEXT,
                errors_json   TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_alerts_at ON alerts (at_ticks DESC);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_alerts_monitor ON alerts (monitor_id, at_ticks DESC);");

        // Настройки ключ-значение. Появились здесь ради каналов оповещения: адрес
        // webhook и параметры почты негде было держать. Пометка secret означает, что
        // значение лежит зашифрованным средствами Windows и в резервной копии,
        // унесённой на другую машину, не раскроется.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS settings (
                key           TEXT    NOT NULL PRIMARY KEY,
                value         TEXT,
                secret        INTEGER NOT NULL DEFAULT 0,
                updated_ticks INTEGER NOT NULL
            );
            """);
    }

    private static void UpgradeToVersion7(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS agents (
                thumbprint     TEXT    NOT NULL PRIMARY KEY,
                machine_name   TEXT    NOT NULL,
                product        TEXT    NOT NULL,
                address        TEXT,
                port           INTEGER NOT NULL DEFAULT 0,
                direction      INTEGER NOT NULL,
                paired_ticks   INTEGER NOT NULL,
                last_seen_ticks INTEGER,
                capabilities   TEXT    NOT NULL DEFAULT '',
                alias          TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_agents_paired ON agents (paired_ticks DESC);");

        // Личность самого клиента живёт здесь же: она одна на установку, и терять её
        // нельзя — новая означала бы потерю всех сопряжений разом.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS client_identity (
                id           INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                container     BLOB    NOT NULL,
                created_ticks INTEGER NOT NULL
            );
            """);
    }

    private static void UpgradeToVersion6(SqliteConnection connection)
    {
        // Ключ по псевдониму, а не по паре: одно тождество может присоединиться
        // только к одному устройству, иначе объединение перестаёт быть однозначным.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS device_aliases (
                alias        TEXT NOT NULL PRIMARY KEY,
                primary_id   TEXT NOT NULL,
                at_ticks     INTEGER NOT NULL,
                operator_name TEXT NOT NULL
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_aliases_primary ON device_aliases (primary_id);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS topology_edits (
                id            TEXT NOT NULL PRIMARY KEY,
                kind          INTEGER NOT NULL,
                subject       TEXT NOT NULL,
                target        TEXT,
                at_ticks      INTEGER NOT NULL,
                operator_name TEXT NOT NULL,
                note          TEXT
            );
            """);
    }

    /// <summary>
    /// Добавляет колонку, если её ещё нет.
    /// </summary>
    /// <remarks>
    /// У SQLite нет «ALTER TABLE ADD COLUMN IF NOT EXISTS», а нужен он ровно так же,
    /// как «CREATE TABLE IF NOT EXISTS», которым пользуются остальные ступени.
    /// Без проверки повторный проход ступени падает на «duplicate column name»,
    /// и база, у которой отметка версии почему-либо отстала от схемы, перестаёт
    /// открываться навсегда.
    /// <para>
    /// Помощник существовал с четвёртой ступени, но девятая (вид пресета, И-14)
    /// прошла мимо него прямым ALTER. Поймал это тест обновления, появившийся
    /// в И-15, — и только он: обычные проверки хранилища всегда начинают с пустого
    /// файла и обновление не трогают вовсе.
    /// </para>
    /// </remarks>
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
