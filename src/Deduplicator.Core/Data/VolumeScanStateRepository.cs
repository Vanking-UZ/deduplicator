using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Доступ к состоянию USN-журнала тома (инкрементальный скан, раздел 4).</summary>
public sealed class VolumeScanStateRepository
{
    private readonly Database _db;
    public VolumeScanStateRepository(Database db) => _db = db;

    public VolumeScanState? Get(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT volume_id, journal_id, next_usn, indexed_at, engine_version, options_hash " +
            "FROM volume_scan_state WHERE volume_id=$v";
        cmd.Add("$v", volumeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VolumeScanState
        {
            VolumeId = r.Long(0),
            JournalId = r.Long(1),
            NextUsn = r.Long(2),
            IndexedAt = Db.ParseDateOrNull(r.Str(3)),
            EngineVersion = (int)r.Long(4),
            OptionsHash = r.Str(5)
        };
    }

    public void Upsert(VolumeScanState state)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_scan_state (volume_id, journal_id, next_usn, indexed_at, engine_version, options_hash)
            VALUES ($v, $j, $u, $i, $e, $o)
            ON CONFLICT(volume_id) DO UPDATE SET
                journal_id=excluded.journal_id, next_usn=excluded.next_usn, indexed_at=excluded.indexed_at,
                engine_version=excluded.engine_version, options_hash=excluded.options_hash;
            """;
        cmd.Add("$v", state.VolumeId);
        cmd.Add("$j", state.JournalId);
        cmd.Add("$u", state.NextUsn);
        cmd.Add("$i", (object?)Db.IsoOrNull(state.IndexedAt) ?? DBNull.Value);
        cmd.Add("$e", state.EngineVersion);
        cmd.Add("$o", (object?)state.OptionsHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM volume_scan_state WHERE volume_id=$v";
        cmd.Add("$v", volumeId);
        cmd.ExecuteNonQuery();
    }
}
