namespace Deduplicator.Core.Data;

/// <summary>DDL схемы SQLite (раздел 8 ТЗ, расширен таблицей сессий сканирования).</summary>
internal static class Schema
{
    public const string Sql = """
    PRAGMA foreign_keys = ON;

    CREATE TABLE IF NOT EXISTS volumes (
        id           INTEGER PRIMARY KEY,
        label        TEXT,
        serial       TEXT,
        fs_type      TEXT,
        total_bytes  INTEGER,
        first_seen   TEXT,
        last_seen    TEXT,
        last_scanned TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_volumes_serial ON volumes(serial);

    CREATE TABLE IF NOT EXISTS files (
        id           INTEGER PRIMARY KEY,
        volume_id    INTEGER NOT NULL REFERENCES volumes(id) ON DELETE CASCADE,
        rel_path     TEXT NOT NULL,
        name         TEXT NOT NULL,
        ext          TEXT,
        size         INTEGER NOT NULL,
        mtime        TEXT NOT NULL,
        created      TEXT,
        category     TEXT,
        partial_hash BLOB,
        full_hash    BLOB,
        phash        BLOB,
        pixel_hash   BLOB,
        audio_fp     BLOB,
        scan_state   TEXT,
        last_indexed TEXT,
        partial_ver  INTEGER NOT NULL DEFAULT 1,
        phash_ver    INTEGER NOT NULL DEFAULT 1,
        -- 2.5: кэш побайтовой сверки. НЕ NULL и байтово равен full_hash — файл уже проходил
        -- сверку чтением против содержимого с этим хэшем; валиден только пока метаданные строки
        -- строго переиспользуются (любой Upsert/пересчёт full_hash обнуляет, см. FileRepository).
        verified_full_hash BLOB,
        -- 3.7: резкость кадра (дисперсия Лапласиана, считается вместе с pHash) — подсказка
        -- «лучший кадр серии» для похожих фото. NULL — не считалась (старые строки не пересчитываем).
        sharpness REAL,
        UNIQUE(volume_id, rel_path)
    );
    CREATE INDEX IF NOT EXISTS idx_files_size     ON files(size);
    CREATE INDEX IF NOT EXISTS idx_files_fullhash ON files(full_hash);
    CREATE INDEX IF NOT EXISTS idx_files_partial  ON files(partial_hash);
    CREATE INDEX IF NOT EXISTS idx_files_category ON files(category);
    CREATE INDEX IF NOT EXISTS idx_files_volume   ON files(volume_id);
    -- Ленивый офлайн-проводник: prefix-range по нормализованному пути не сканирует весь том
    -- при каждом раскрытии вложенной папки. replace/char — детерминированные функции SQLite.
    CREATE INDEX IF NOT EXISTS idx_files_volume_rel_norm
        ON files(volume_id, replace(rel_path, char(92), '/') COLLATE NOCASE);
    -- Составные индексы для быстрой воронки exact-скана:
    -- потоковая выборка кандидатов по размеру и группировка по (size, hash) без N+1 запросов.
    CREATE INDEX IF NOT EXISTS idx_files_size_id       ON files(size, id);
    CREATE INDEX IF NOT EXISTS idx_files_size_partial  ON files(size, partial_hash);
    CREATE INDEX IF NOT EXISTS idx_files_size_full     ON files(size, full_hash);
    CREATE INDEX IF NOT EXISTS idx_files_volume_size   ON files(volume_id, size);
    CREATE INDEX IF NOT EXISTS idx_files_category_state ON files(category, scan_state);

    CREATE TABLE IF NOT EXISTS duplicate_groups (
        id              INTEGER PRIMARY KEY,
        match_type      TEXT,
        confidence      REAL,
        representative  INTEGER REFERENCES files(id) ON DELETE SET NULL,
        total_bytes     INTEGER,
        created_at      TEXT,
        scan_session_id INTEGER
    );
    CREATE INDEX IF NOT EXISTS idx_groups_type ON duplicate_groups(match_type);
    CREATE INDEX IF NOT EXISTS idx_groups_session_type ON duplicate_groups(scan_session_id, match_type);

    CREATE TABLE IF NOT EXISTS duplicate_members (
        group_id   INTEGER NOT NULL REFERENCES duplicate_groups(id) ON DELETE CASCADE,
        file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        similarity REAL,
        PRIMARY KEY (group_id, file_id)
    );
    CREATE INDEX IF NOT EXISTS idx_members_file ON duplicate_members(file_id);

    CREATE TABLE IF NOT EXISTS lsh_buckets (
        kind       TEXT,
        band       INTEGER,
        bucket_key INTEGER,
        file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_lsh ON lsh_buckets(kind, band, bucket_key);

    CREATE TABLE IF NOT EXISTS operations_log (
        id           INTEGER PRIMARY KEY,
        op_type      TEXT,
        file_id      INTEGER,
        group_id     INTEGER,
        src_path     TEXT,
        dst_path     TEXT,
        performed_at TEXT,
        reversible   INTEGER,
        restored     INTEGER DEFAULT 0,
        size_bytes   INTEGER,
        file_name    TEXT
    );

    CREATE TABLE IF NOT EXISTS scan_sessions (
        id           INTEGER PRIMARY KEY,
        options_json TEXT,
        stage        INTEGER,
        started_at   TEXT,
        finished_at  TEXT,
        files_seen   INTEGER DEFAULT 0,
        files_skipped INTEGER DEFAULT 0
    );

    -- Состояние USN-журнала тома для инкрементального скана: по journal_id+next_usn
    -- следующий скан понимает, можно ли переиспользовать индекс или нужен полный пересбор.
    CREATE TABLE IF NOT EXISTS volume_scan_state (
        volume_id      INTEGER PRIMARY KEY REFERENCES volumes(id) ON DELETE CASCADE,
        journal_id     INTEGER,
        next_usn       INTEGER,
        indexed_at     TEXT,
        engine_version INTEGER,
        options_hash   TEXT
    );

    -- Явный маркер области нужен отдельно от строк файлов: пустая выбранная папка — это корректная
    -- ПУСТАЯ область, а не сигнал откатиться к глобальной исторической базе.
    CREATE TABLE IF NOT EXISTS scan_scope_sessions (
        scan_session_id INTEGER PRIMARY KEY
    );

    -- Файлы в области текущего скана (P1.2): кандидаты для выбранных дисков/папок считаются только
    -- по этим строкам, а не по всей таблице files. Безопасность от пропусков: scope заполняется
    -- полным набором просмотренных in-scope файлов, а итоговую корректность гарантирует inScope.
    CREATE TABLE IF NOT EXISTS scan_scope_files (
        scan_session_id INTEGER NOT NULL,
        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        size            INTEGER NOT NULL,
        category        INTEGER NOT NULL,
        PRIMARY KEY (scan_session_id, file_id)
    );
    CREATE INDEX IF NOT EXISTS idx_scan_scope_size ON scan_scope_files(scan_session_id, size);
    CREATE INDEX IF NOT EXISTS idx_scan_scope_cat_size ON scan_scope_files(scan_session_id, category, size);

    -- Для неизменившегося/USN-обновлённого тома не копируем миллионы file_id во временную
    -- таблицу: достаточно компактного дескриптора «сессия + том + категория».
    CREATE TABLE IF NOT EXISTS scan_scope_volumes (
        scan_session_id INTEGER NOT NULL,
        volume_id      INTEGER NOT NULL REFERENCES volumes(id) ON DELETE CASCADE,
        category       TEXT NOT NULL,
        PRIMARY KEY (scan_session_id, volume_id, category)
    );
    CREATE INDEX IF NOT EXISTS idx_scan_scope_volumes_session ON scan_scope_volumes(scan_session_id, volume_id);

    -- Физические удаления из канонического индекса откладываются до атомарной публикации успешной
    -- сессии. Так отмена/сбой не каскадирует удаление членов последнего готового результата.
    CREATE TABLE IF NOT EXISTS scan_pending_file_deletes (
        scan_session_id INTEGER NOT NULL,
        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        PRIMARY KEY (scan_session_id, file_id)
    );
    CREATE INDEX IF NOT EXISTS idx_pending_delete_file ON scan_pending_file_deletes(file_id);

    CREATE VIEW IF NOT EXISTS scan_scope_effective AS
        SELECT scan_session_id, file_id, size, category FROM scan_scope_files
        UNION ALL
        SELECT sv.scan_session_id, f.id, f.size, f.category
        FROM scan_scope_volumes sv
        JOIN files f ON f.volume_id=sv.volume_id AND f.category=sv.category
        WHERE NOT EXISTS (
            SELECT 1 FROM scan_scope_files sf
            WHERE sf.scan_session_id=sv.scan_session_id AND sf.file_id=f.id
        )
        AND NOT EXISTS (
            SELECT 1 FROM scan_pending_file_deletes pd
            WHERE pd.scan_session_id=sv.scan_session_id AND pd.file_id=f.id
        );
    """;

    /// <summary>
    /// Миграции добавления колонок в существующие базы. Применяются только если колонки нет
    /// (проверка через PRAGMA table_info) — без ловли исключений как управляющего потока.
    /// </summary>
    public static readonly (string Table, string Column, string Ddl)[] ColumnMigrations =
    {
        ("files", "created", "ALTER TABLE files ADD COLUMN created TEXT"),
        ("duplicate_groups", "scan_session_id", "ALTER TABLE duplicate_groups ADD COLUMN scan_session_id INTEGER"),
        // Версия алгоритма частичного хэша (этап 1.6): v2 добавляет средний блок для файлов >128 МБ.
        // Старые строки остаются v1 и пересчитываются ТОЛЬКО для крупных (у мелких алгоритм не менялся).
        ("files", "partial_ver", "ALTER TABLE files ADD COLUMN partial_ver INTEGER NOT NULL DEFAULT 1"),
        // Версия алгоритма pHash (этап 2.2): v2 декодирует в TargetSize 256×256 — значения сдвигаются
        // на единицы бит, старые строки (v1) пересчитываются, чтобы не смешивать версии в группировке.
        ("files", "phash_ver", "ALTER TABLE files ADD COLUMN phash_ver INTEGER NOT NULL DEFAULT 1"),
        // Кэш побайтовой сверки (этап 2.5): старые строки получают NULL — «не сверялся», первый скан
        // сверит чтением и заполнит; повторные сканы неизменённых файлов пойдут без чтения содержимого.
        ("files", "verified_full_hash", "ALTER TABLE files ADD COLUMN verified_full_hash BLOB"),
        // Резкость кадра (3.7): старые строки получают NULL — пересчёт НЕ форсируем (это лишь
        // подсказка UI; бейдж «Лучший кадр» просто не покажется до следующего пересчёта фото).
        ("files", "sharpness", "ALTER TABLE files ADD COLUMN sharpness REAL")
    };
}
