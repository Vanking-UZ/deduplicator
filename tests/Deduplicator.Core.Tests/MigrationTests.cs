using Deduplicator.Core.Data;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public class MigrationTests : IDisposable
{
    private readonly string _dir;
    public MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Initialize_is_idempotent_and_created_date_roundtrips()
    {
        var db = new Database(Path.Combine(_dir, "m.db"));
        db.Initialize();
        db.Initialize(); // повторно — не должно бросать (миграция применяется только при отсутствии колонки)

        using (var conn = db.Open())
        using (var version = conn.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(Database.CurrentSchemaVersion, Convert.ToInt32(version.ExecuteScalar()));
        }

        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);

        long vid = volumes.Upsert(new VolumeRecord { Serial = "MIG", Label = "test" });
        var created = new DateTimeOffset(2020, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var rec = new FileRecord
        {
            VolumeId = vid,
            RelPath = "a.txt",
            Name = "a.txt",
            Size = 1,
            Mtime = DateTimeOffset.Now,
            Created = created,
            Category = FileCategory.Other
        };
        files.UpsertMetaBatch(new[] { rec });

        var back = files.GetByVolumeAndPath(vid, "a.txt");
        Assert.NotNull(back);
        Assert.Equal(created, back!.Created);
    }

    [Fact]
    public void Upsert_same_serial_is_idempotent()
    {
        var db = new Database(Path.Combine(_dir, "u.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);

        long id1 = volumes.Upsert(new VolumeRecord { Serial = "SER-1", Label = "first" });
        long id2 = volumes.Upsert(new VolumeRecord { Serial = "SER-1", Label = "updated" });

        Assert.Equal(id1, id2);
        var all = volumes.GetAll();
        Assert.Single(all);
        Assert.Equal("updated", all[0].Label); // ON CONFLICT обновил метку
    }

    [Fact]
    public async Task Parallel_upsert_same_serial_yields_single_row()
    {
        var db = new Database(Path.Combine(_dir, "p.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);

        // Воспроизводим гонку: множество одновременных синхронизаций одного и того же диска.
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            volumes.Upsert(new VolumeRecord { Serial = "RACE", Label = "disk" + i }))).ToArray();
        var ids = await Task.WhenAll(tasks);

        Assert.Single(ids.Distinct());            // все вернули один и тот же Id
        Assert.Single(volumes.GetAll().Where(v => v.Serial == "RACE")); // ровно одна строка
    }

    [Fact]
    public void Migration_collapses_legacy_duplicate_volumes()
    {
        var path = Path.Combine(_dir, "legacy.db");

        // Готовим «старую» БД: схема БЕЗ UNIQUE-индекса + две строки одного диска (как до фикса).
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = """
                CREATE TABLE volumes (id INTEGER PRIMARY KEY, label TEXT, serial TEXT, fs_type TEXT,
                    total_bytes INTEGER, first_seen TEXT, last_seen TEXT, last_scanned TEXT);
                CREATE TABLE files (id INTEGER PRIMARY KEY,
                    volume_id INTEGER NOT NULL REFERENCES volumes(id) ON DELETE CASCADE,
                    rel_path TEXT NOT NULL, name TEXT NOT NULL, ext TEXT, size INTEGER NOT NULL,
                    mtime TEXT NOT NULL, created TEXT, category TEXT,
                    partial_hash BLOB, full_hash BLOB, phash BLOB, pixel_hash BLOB, audio_fp BLOB,
                    scan_state TEXT, last_indexed TEXT, UNIQUE(volume_id, rel_path));
                INSERT INTO volumes (id, label, serial) VALUES (1, 'Disk', 'DUP-XYZ');
                INSERT INTO volumes (id, label, serial) VALUES (2, 'Disk', 'DUP-XYZ');
                INSERT INTO files (id, volume_id, rel_path, name, size, mtime)
                    VALUES (10, 2, 'sub/a.txt', 'a.txt', 5, '2020-01-01');
                """;
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Инициализация новой версии запускает миграцию: дедуп + UNIQUE-индекс.
        var db = new Database(path);
        db.Initialize();

        Assert.NotNull(db.LastMigrationBackupPath);
        Assert.True(File.Exists(db.LastMigrationBackupPath), "Перед обновлением legacy-БД должен быть создан backup");

        var volumes = new VolumeRepository(db);
        var rows = volumes.GetAll().Where(v => v.Serial == "DUP-XYZ").ToList();
        Assert.Single(rows);                       // дубликат схлопнут
        Assert.Equal(1, rows[0].IndexedFileCount); // файл перенаправлен на выжившую запись (id=1)

        // Повторная инициализация и Upsert того же серийника не создают новых строк.
        db.Initialize();
        volumes.Upsert(new VolumeRecord { Serial = "DUP-XYZ", Label = "Disk" });
        Assert.Single(volumes.GetAll().Where(v => v.Serial == "DUP-XYZ"));
    }

    [Fact]
    public void Initialize_rejects_database_from_newer_application_without_rewriting_it()
    {
        var path = Path.Combine(_dir, "future.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE sentinel(value TEXT); " +
                              "INSERT INTO sentinel VALUES('keep'); " +
                              $"PRAGMA user_version={Database.CurrentSchemaVersion + 1};";
            cmd.ExecuteNonQuery();
        }

        var db = new Database(path);
        var error = Assert.Throws<InvalidOperationException>(db.Initialize);
        Assert.Contains("более новой версией", error.Message);
        Assert.Null(db.LastMigrationBackupPath);

        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        verify.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT value FROM sentinel";
        Assert.Equal("keep", read.ExecuteScalar());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
