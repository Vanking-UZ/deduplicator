using Microsoft.Data.Sqlite;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Каталог носителей (томов).</summary>
public sealed class VolumeRepository
{
    private readonly Database _db;
    public VolumeRepository(Database db) => _db = db;

    private const string Cols =
        "id, label, serial, fs_type, total_bytes, first_seen, last_seen, last_scanned";

    /// <summary>Найти существующий том по серийному номеру (узнавание носителя, FR-2).</summary>
    public VolumeRecord? FindBySerial(string serial)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM volumes WHERE serial = $s LIMIT 1";
        cmd.Add("$s", serial);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>
    /// Зарегистрировать или обновить том по серийному номеру; возвращает Id.
    /// Поиск и вставка выполняются под транзакцией <c>BEGIN IMMEDIATE</c> (эксклюзивная блокировка
    /// записи), поэтому параллельные синхронизации одного диска больше не плодят дубликаты —
    /// независимо от наличия UNIQUE-индекса.
    /// </summary>
    public long Upsert(VolumeRecord v)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false); // IMMEDIATE — сериализуем поиск+запись

        long? existingId = null;
        if (!string.IsNullOrEmpty(v.Serial))
        {
            using var find = conn.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM volumes WHERE serial = $s LIMIT 1";
            find.Add("$s", v.Serial);
            existingId = (long?)find.ExecuteScalar();
        }

        long id;
        if (existingId is long eid)
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE volumes SET label=$label, fs_type=$fs, total_bytes=$tb, last_seen=$ls
                WHERE id=$id
                """;
            upd.Add("$label", v.Label);
            upd.Add("$fs", v.FsType);
            upd.Add("$tb", v.TotalBytes);
            upd.Add("$ls", Db.IsoNow());
            upd.Add("$id", eid);
            upd.ExecuteNonQuery();
            id = eid;
        }
        else
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO volumes (label, serial, fs_type, total_bytes, first_seen, last_seen, last_scanned)
                VALUES ($label, $serial, $fs, $tb, $fseen, $lseen, NULL);
                SELECT last_insert_rowid();
                """;
            ins.Add("$label", v.Label);
            ins.Add("$serial", v.Serial);
            ins.Add("$fs", v.FsType);
            ins.Add("$tb", v.TotalBytes);
            ins.Add("$fseen", Db.IsoNow());
            ins.Add("$lseen", Db.IsoNow());
            id = (long)ins.ExecuteScalar()!;
        }

        tx.Commit();
        return id;
    }

    public List<VolumeRecord> GetAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM volumes ORDER BY last_seen DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<VolumeRecord>();
        while (r.Read()) list.Add(Map(r));

        // Дополнить количеством проиндексированных файлов.
        foreach (var v in list)
            v.IndexedFileCount = CountFiles(conn, v.Id);
        return list;
    }

    public VolumeRecord? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM volumes WHERE id = $id";
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var v = Map(r);
        v.IndexedFileCount = CountFiles(conn, id);
        return v;
    }

    /// <summary>Удалить носитель из каталога вместе со всеми его слепками (каскадно).</summary>
    public void Delete(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM volumes WHERE id=$id";
        cmd.Add("$id", volumeId);
        cmd.ExecuteNonQuery();
    }

    public void MarkScanned(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE volumes SET last_scanned=$t, last_seen=$t WHERE id=$id";
        cmd.Add("$t", Db.IsoNow());
        cmd.Add("$id", volumeId);
        cmd.ExecuteNonQuery();
    }

    private static long CountFiles(SqliteConnection conn, long volumeId)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM files WHERE volume_id = $v";
        c.Add("$v", volumeId);
        return (long)c.ExecuteScalar()!;
    }

    private static VolumeRecord Map(SqliteDataReader r) => new()
    {
        Id = r.Long(0),
        Label = r.Str(1),
        Serial = r.Str(2),
        FsType = r.Str(3),
        TotalBytes = r.Long(4),
        FirstSeen = Db.ParseDate(r.Str(5)),
        LastSeen = Db.ParseDate(r.Str(6)),
        LastScanned = Db.ParseDateOrNull(r.Str(7))
    };
}
