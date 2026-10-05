using Microsoft.Data.Sqlite;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Data;

/// <summary>
/// Управление подключениями к SQLite (режим WAL для краш-устойчивости и параллельного чтения, NFR-2).
/// Каждое подключение короткоживущее; запись — через транзакции.
/// </summary>
public sealed class Database
{
    public const int CurrentSchemaVersion = 1;
    private const int MigrationBackupsToKeep = 3;

    public string Path { get; }
    public string? LastMigrationBackupPath { get; private set; }
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        Path = dbPath;
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>Открывает подготовленное подключение (с применёнными PRAGMA).</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 30000;
                PRAGMA temp_store = MEMORY;
                """;
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>
    /// Создаёт/обновляет схему атомарно. Перед изменением существующей базы создаётся согласованный
    /// SQLite-backup; при ошибке миграции он автоматически возвращается на место.
    /// </summary>
    public void Initialize()
    {
        string? backupPath = null;
        bool hadDatabase = File.Exists(Path) && new FileInfo(Path).Length > 0;

        try
        {
            using var conn = Open();
            EnsureIntegrity(conn, "до миграции");

            int currentVersion = GetUserVersion(conn);
            if (currentVersion > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"База данных создана более новой версией приложения (схема {currentVersion}, " +
                    $"поддерживается до {CurrentSchemaVersion}).");

            if (hadDatabase && currentVersion < CurrentSchemaVersion)
            {
                backupPath = CreateMigrationBackup(conn, currentVersion);
                LastMigrationBackupPath = backupPath;
            }

            using (var tx = conn.BeginTransaction())
            {
                // Миграции — ДО основной схемы. Schema.Sql создаёт индексы по колонкам, которые на
                // старой базе появляются только миграцией. Вся последовательность теперь либо
                // фиксируется целиком, либо целиком откатывается.
                foreach (var (table, column, ddl) in Schema.ColumnMigrations)
                {
                    if (!TableExists(conn, table, tx)) continue;
                    if (ColumnExists(conn, table, column, tx)) continue;
                    using var migration = conn.CreateCommand();
                    migration.Transaction = tx;
                    migration.CommandText = ddl;
                    migration.ExecuteNonQuery();
                }

                using var schema = conn.CreateCommand();
                schema.Transaction = tx;
                schema.CommandText = Schema.Sql;
                schema.ExecuteNonQuery();

                using var version = conn.CreateCommand();
                version.Transaction = tx;
                version.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion}";
                version.ExecuteNonQuery();
                tx.Commit();
            }

            // Носители: устранить дубли по серийнику и навесить UNIQUE-индекс. Эта старая
            // data-migration идемпотентна и выполняется после появления всех таблиц.
            DeduplicateVolumesAndEnforceUniqueSerial(conn);
            EnsureIntegrity(conn, "после миграции");
            TrimMigrationBackups();
        }
        catch
        {
            if (backupPath is not null)
                RestoreMigrationBackup(backupPath);
            throw;
        }
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void EnsureIntegrity(SqliteConnection conn, string stage)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        var result = Convert.ToString(cmd.ExecuteScalar()) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Проверка целостности базы {stage} завершилась ошибкой: {result}");
    }

    private string CreateMigrationBackup(SqliteConnection source, int fromVersion)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string backupPath = $"{Path}.pre-v{fromVersion}-{stamp}.bak";
        var backupConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var destination = new SqliteConnection(backupConnectionString);
        destination.Open();
        source.BackupDatabase(destination);
        AppLog.Write($"DB migration backup created: {System.IO.Path.GetFileName(backupPath)}");
        return backupPath;
    }

    private void RestoreMigrationBackup(string backupPath)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backupPath, Path, overwrite: true);
            TryDelete(Path + "-wal");
            TryDelete(Path + "-shm");
            AppLog.Write($"DB migration rolled back from {System.IO.Path.GetFileName(backupPath)}");
        }
        catch (Exception restoreError)
        {
            AppLog.Error("Database.RestoreMigrationBackup", restoreError);
        }
    }

    private void TrimMigrationBackups()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            var fileName = System.IO.Path.GetFileName(Path);
            if (string.IsNullOrEmpty(directory)) return;
            foreach (var old in Directory.EnumerateFiles(directory, fileName + ".pre-v*.bak")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(MigrationBackupsToKeep))
                TryDelete(old);
        }
        catch (Exception ex) { AppLog.Error("Database.TrimMigrationBackups", ex); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Одноразовая миграция: схлопывает дублирующиеся строки <c>volumes</c> с одинаковым серийником
    /// (файлы перенаправляются на «выжившую» запись), затем создаёт UNIQUE-индекс по serial.
    /// Дедуп выполняется в C# (сравнение строк надёжно и не зависит от особенностей сравнения в БД).
    /// Полностью защищена try/catch — сбой чистки НИКОГДА не должен ронять запуск приложения.
    /// </summary>
    private static void DeduplicateVolumesAndEnforceUniqueSerial(SqliteConnection conn)
    {
        try
        {
            if (IndexExists(conn, "ux_volumes_serial")) return;

            // 1) Найти дубли по нормализованному серийнику (в C#, не средствами SQL).
            var keepBySerial = new Dictionary<string, long>(StringComparer.Ordinal);
            var losers = new List<(long Loser, long Keep)>();
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT id, serial FROM volumes WHERE serial IS NOT NULL ORDER BY id";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    string serial = (r.IsDBNull(1) ? "" : r.GetString(1)).Trim();
                    if (serial.Length == 0) continue;
                    if (keepBySerial.TryGetValue(serial, out var keep)) losers.Add((id, keep));
                    else keepBySerial[serial] = id;
                }
            }

            // 2) Перенаправить файлы дублей на выжившую запись и удалить лишние строки.
            if (losers.Count > 0)
            {
                using var tx = conn.BeginTransaction();
                foreach (var (loser, keep) in losers)
                {
                    Exec(conn, tx,
                        "DELETE FROM files WHERE volume_id=$l AND rel_path IN (SELECT rel_path FROM files WHERE volume_id=$k)",
                        ("$l", loser), ("$k", keep));
                    Exec(conn, tx, "UPDATE files SET volume_id=$k WHERE volume_id=$l", ("$k", keep), ("$l", loser));
                    Exec(conn, tx, "DELETE FROM volumes WHERE id=$l", ("$l", loser));
                }
                tx.Commit();
            }

            // 3) UNIQUE-индекс (после дедупа). Best-effort: если вдруг не создастся —
            //    атомарность Upsert обеспечивается транзакцией BEGIN IMMEDIATE и без него.
            using var ix = conn.CreateCommand();
            ix.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_volumes_serial ON volumes(serial)";
            ix.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try { Deduplicator.Core.Util.AppLog.Error("VolumeDedupMigration", ex); } catch { /* лог недоступен — не критично */ }
        }
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static bool IndexExists(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", indexName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool TableExists(SqliteConnection conn, string table, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // PRAGMA table_info: cid(0), name(1), type(2)...
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Сжать WAL в основной файл (вызывать при простое/закрытии).</summary>
    public void Checkpoint()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* checkpoint — оптимизация, не критично */ }
    }

    /// <summary>
    /// Лёгкий неблокирующий чекпоинт (PASSIVE) — вызывать между пачками во время долгой воронки,
    /// когда нет открытых reader-ов, чтобы WAL не рос на весь скан. В отличие от TRUNCATE не ждёт
    /// читателей и не копирует весь WAL целиком.
    /// </summary>
    public void PassiveCheckpoint()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* чекпоинт — оптимизация, не критично */ }
    }
}
