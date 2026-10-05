using Microsoft.Data.Sqlite;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

public sealed record OperationLogPage(IReadOnlyList<OperationLogEntry> Items, long TotalCount, long? NextBeforeId);

/// <summary>Журнал операций — основа обратимости (FR-32).</summary>
public sealed class OperationLogRepository
{
    private readonly Database _db;
    public OperationLogRepository(Database db) => _db = db;

    /// <summary>
    /// Сколько байт освобождено программой за всё время (перемещено в корзину/карантин и не
    /// восстановлено). «Вы освободили N ГБ» на Главной — этап 1.10 дорожной карты.
    /// </summary>
    public long GetTotalFreedBytes()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(size_bytes), 0) FROM operations_log
            WHERE op_type IN ($rec, $quar) AND restored = 0
            """;
        cmd.Add("$rec", Mappers.OpTypeToDb(Models.OperationType.DeleteToRecycle));
        cmd.Add("$quar", Mappers.OpTypeToDb(Models.OperationType.MoveToQuarantine));
        return (long)cmd.ExecuteScalar()!;
    }

    public long Insert(OperationLogEntry e)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operations_log
                (op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name)
            VALUES ($op, $fid, $gid, $src, $dst, $ts, $rev, $restored, $size, $name);
            SELECT last_insert_rowid();
            """;
        cmd.Add("$op", Mappers.OpTypeToDb(e.OpType));
        cmd.Add("$fid", e.FileId);
        cmd.Add("$gid", e.GroupId);
        cmd.Add("$src", e.SrcPath);
        cmd.Add("$dst", e.DstPath);
        cmd.Add("$ts", Db.Iso(e.PerformedAt == default ? DateTimeOffset.Now : e.PerformedAt));
        cmd.Add("$rev", e.Reversible ? 1 : 0);
        cmd.Add("$restored", e.Restored ? 1 : 0);
        cmd.Add("$size", e.SizeBytes);
        cmd.Add("$name", e.FileName);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<OperationLogEntry> GetAll(bool onlyReversible = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name " +
            "FROM operations_log " +
            (onlyReversible ? "WHERE reversible=1 AND restored=0 " : "") +
            "ORDER BY performed_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<OperationLogEntry>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Keyset-страница журнала. Стоимость следующей страницы не растёт вместе с размером таблицы,
    /// в отличие от OFFSET; новые операции сверху не сдвигают уже показанные строки.
    /// </summary>
    public OperationLogPage GetPage(int limit, long? beforeId = null, bool onlyReversible = false)
    {
        limit = Math.Clamp(limit, 1, 500);
        using var conn = _db.Open();

        long total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM operations_log " +
                                (onlyReversible ? "WHERE reversible=1 AND restored=0" : "");
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();
        if (onlyReversible) conditions.Add("reversible=1 AND restored=0");
        if (beforeId is not null) conditions.Add("id < $before");
        cmd.CommandText =
            "SELECT id, op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name " +
            "FROM operations_log " +
            (conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) + " " : "") +
            "ORDER BY id DESC LIMIT $limit";
        cmd.Add("$limit", limit);
        if (beforeId is not null) cmd.Add("$before", beforeId.Value);

        using var r = cmd.ExecuteReader();
        var items = new List<OperationLogEntry>(limit);
        while (r.Read()) items.Add(Map(r));
        long? next = items.Count == limit ? items[^1].Id : null;
        return new OperationLogPage(items, total, next);
    }

    public OperationLogEntry? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name " +
            "FROM operations_log WHERE id=$id";
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public void MarkRestored(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE operations_log SET restored=1 WHERE id=$id";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удалить запись журнала (откат упреждающей записи, если операция не состоялась).</summary>
    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM operations_log WHERE id=$id";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static OperationLogEntry Map(SqliteDataReader r) => new()
    {
        Id = r.Long(0),
        OpType = Mappers.ParseOpType(r.Str(1)),
        FileId = r.LongN(2),
        GroupId = r.LongN(3),
        SrcPath = r.Str(4),
        DstPath = r.Str(5),
        PerformedAt = Db.ParseDate(r.Str(6)),
        Reversible = r.Bool(7),
        Restored = r.Bool(8),
        SizeBytes = r.LongN(9),
        FileName = r.Str(10)
    };
}
