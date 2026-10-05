using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Корзины LSH для быстрого поиска похожих (раздел 7 ТЗ).</summary>
public sealed class LshRepository
{
    private readonly Database _db;
    public LshRepository(Database db) => _db = db;

    private static string KindName(MatchType kind) =>
        kind == MatchType.SimilarAudio ? "audio" : "image";

    public void ClearKind(MatchType kind)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM lsh_buckets WHERE kind=$k";
        cmd.Add("$k", KindName(kind));
        cmd.ExecuteNonQuery();
    }

    public void InsertBuckets(MatchType kind, IEnumerable<(int band, long bucketKey, long fileId)> rows)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO lsh_buckets (kind, band, bucket_key, file_id) VALUES ($k, $b, $key, $f)";
        var pK = cmd.CreateParameter(); pK.ParameterName = "$k"; pK.Value = KindName(kind); cmd.Parameters.Add(pK);
        var pB = cmd.CreateParameter(); pB.ParameterName = "$b"; cmd.Parameters.Add(pB);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
        var pF = cmd.CreateParameter(); pF.ParameterName = "$f"; cmd.Parameters.Add(pF);
        foreach (var (band, key, fileId) in rows)
        {
            pB.Value = band;
            pKey.Value = key;
            pF.Value = fileId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Все строки корзин данного вида — для построения карты кандидатов в памяти.</summary>
    public List<(int band, long bucketKey, long fileId)> GetAll(MatchType kind)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT band, bucket_key, file_id FROM lsh_buckets WHERE kind=$k";
        cmd.Add("$k", KindName(kind));
        using var r = cmd.ExecuteReader();
        var list = new List<(int, long, long)>();
        while (r.Read()) list.Add(((int)r.Long(0), r.Long(1), r.Long(2)));
        return list;
    }
}
