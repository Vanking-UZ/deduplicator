using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Сессии сканирования — для возобновляемости (FR-8, NFR-2).</summary>
public sealed class ScanSessionRepository
{
    private readonly Database _db;
    public ScanSessionRepository(Database db) => _db = db;

    public long Create(string optionsJson)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scan_sessions (options_json, stage, started_at)
            VALUES ($opt, $stage, $ts);
            SELECT last_insert_rowid();
            """;
        cmd.Add("$opt", optionsJson);
        cmd.Add("$stage", (long)ScanStage.Enumerating);
        cmd.Add("$ts", Db.IsoNow());
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateProgress(long id, ScanStage stage, long filesSeen, long filesSkipped)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE scan_sessions SET stage=$s, files_seen=$fs, files_skipped=$fk WHERE id=$id";
        cmd.Add("$s", (long)stage);
        cmd.Add("$fs", filesSeen);
        cmd.Add("$fk", filesSkipped);
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Finish(long id, ScanStage stage)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE scan_sessions SET stage=$s, finished_at=$ts WHERE id=$id";
        cmd.Add("$s", (long)stage);
        cmd.Add("$ts", Db.IsoNow());
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>ID последней УСПЕШНО завершённой сессии (для чтения только её результатов, P0.2).</summary>
    /// <remarks>Сортировка по id, НЕ по finished_at: даты пишутся строкой с ЛОКАЛЬНЫМ смещением,
    /// и лексикографическое сравнение после смены часового пояса выбирало старую сессию, чьи
    /// группы уже удалены («Нет групп» после успешного скана). id монотонен всегда.</remarks>
    public long? GetLatestCompletedSessionId()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM scan_sessions
            WHERE stage=$done
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Add("$done", (long)ScanStage.Completed);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt64(r);
    }

    /// <summary>Старт приложения (P0.2): незавершённые сессии (приложение убили/упало) помечаем как Failed.</summary>
    public void MarkUnfinishedAsFailed()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scan_sessions SET stage=$fail, finished_at=$ts
            WHERE finished_at IS NULL AND stage NOT IN ($done, $cancel, $fail)
            """;
        cmd.Add("$fail", (long)ScanStage.Failed);
        cmd.Add("$ts", Db.IsoNow());
        cmd.Add("$done", (long)ScanStage.Completed);
        cmd.Add("$cancel", (long)ScanStage.Cancelled);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Найти незавершённую сессию (для предложения возобновить).</summary>
    public ScanSession? GetUnfinished()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, options_json, stage, started_at, finished_at, files_seen, files_skipped
            FROM scan_sessions
            WHERE finished_at IS NULL AND stage NOT IN ($done, $cancel, $fail)
            ORDER BY started_at DESC LIMIT 1
            """;
        cmd.Add("$done", (long)ScanStage.Completed);
        cmd.Add("$cancel", (long)ScanStage.Cancelled);
        cmd.Add("$fail", (long)ScanStage.Failed);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScanSession
        {
            Id = r.Long(0),
            OptionsJson = r.Str(1) ?? "{}",
            Stage = (ScanStage)r.Long(2),
            StartedAt = Db.ParseDate(r.Str(3)),
            FinishedAt = Db.ParseDateOrNull(r.Str(4)),
            FilesSeen = r.Long(5),
            FilesSkipped = r.Long(6)
        };
    }
}
