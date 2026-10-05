// Воспроизводимый перф-харнесс движка точных дубликатов v2 (входит в основной solution) —
// запуск: dotnet run -c Release --project tools/DedupBench -- [enumN] [funnelN] [hashPairs] [hashKB]
//
// Сценарии:
//   A. Перечисление N реальных файлов (скорость enumerate, files/sec).
//   B. Воронка на N синтетических metadata-строках с УНИКАЛЬНЫМИ размерами — доказывает, что
//      контент НЕ читается там, где размер уникален (SizeCandidates=0, FullHashed=0).
//   C. Хэш-пропускная способность на реальных дубль-парах: partial-стадия, full-стадия, байты.
using System.Diagnostics;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using FastDedup.Engine;

int enumN     = Arg(0, 100_000);
int funnelN   = Arg(1, 1_000_000);
int hashPairs = Arg(2, 2_000);
int hashKB    = Arg(3, 128);

var root = Path.Combine(Path.GetTempPath(), "dedupbench_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"DedupBench  enumN={enumN:N0} funnelN={funnelN:N0} hashPairs={hashPairs:N0} hashKB={hashKB}");
Console.WriteLine($"temp: {root}\n");

try
{
    ScenarioEnumerate(enumN);
    ScenarioFunnelUniqueSizes(funnelN);
    ScenarioHashThroughput(hashPairs, hashKB);
    Console.WriteLine($"\nPeak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024):N0} MB");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

// ---------- A. enumerate ----------
void ScenarioEnumerate(int n)
{
    var dir = Path.Combine(root, "enum");
    // Раскладываем по подпапкам, чтобы дерево было реалистичным.
    int perDir = 2000;
    byte[] marker = [0]; // Пустые файлы штатно отфильтровываются: они не экономят место.
    for (int i = 0; i < n; i++)
    {
        if (i % perDir == 0) Directory.CreateDirectory(Path.Combine(dir, "d" + (i / perDir)));
        File.WriteAllBytes(Path.Combine(dir, "d" + (i / perDir), $"f{i}.bin"), marker);
    }
    var lister = new FastFileLister();
    var sw = Stopwatch.StartNew();
    long count = 0;
    foreach (var _ in lister.Enumerate(dir, new FastListerOptions(), CancellationToken.None)) count++;
    sw.Stop();
    Console.WriteLine($"A. ENUMERATE  {count:N0} files in {sw.ElapsedMilliseconds:N0} ms  =>  {count / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} files/sec");
    try { Directory.Delete(dir, true); } catch { }
}

// ---------- B. funnel over unique-size synthetic metadata ----------
void ScenarioFunnelUniqueSizes(int n)
{
    var db = new Database(Path.Combine(root, "funnel.db"));
    db.Initialize();
    var volumes = new VolumeRepository(db);
    var files = new FileRepository(db);
    var hash = new HashService();
    var grouper = new DuplicateGrouper(files, new GroupRepository(db), new LshRepository(db), hash);
    var vol = new VolumeRecord { Serial = "BENCH", MountPath = root, IsConnected = true };
    vol.Id = volumes.Upsert(vol);

    var swInsert = Stopwatch.StartNew();
    var batch = new List<FileRecord>(10_000);
    for (int i = 0; i < n; i++)
    {
        batch.Add(new FileRecord
        {
            VolumeId = vol.Id, RelPath = $"u/{i}.bin", Name = $"{i}.bin",
            Size = i + 1, // уникальный размер у каждого файла
            Mtime = DateTimeOffset.UnixEpoch, Category = FileCategory.Other, ScanState = ScanState.Meta
        });
        if (batch.Count >= 10_000) { files.UpsertMetaBatch(batch); batch.Clear(); }
    }
    if (batch.Count > 0) files.UpsertMetaBatch(batch);
    swInsert.Stop();

    var rep = new TimingReporter();
    var req = new ExactScanRequest
    {
        MountById = new Dictionary<long, string> { [vol.Id] = root },
        VolumesById = new Dictionary<long, VolumeRecord> { [vol.Id] = vol },
        InScope = _ => true,
        ReadPlans = new Dictionary<long, VolumeReadPlan>(),
        MaxVolumeParallelism = 1, RequestedParallelism = 1, Rule = RepresentativeRule.Oldest
    };
    var engine = new ExactDuplicateEngineV2(files, hash, grouper);
    var sw = Stopwatch.StartNew();
    var result = engine.ScanExact(req, rep, CancellationToken.None);
    sw.Stop();

    Console.WriteLine($"B. FUNNEL/1M  inserted {n:N0} meta rows in {swInsert.ElapsedMilliseconds:N0} ms; " +
                      $"candidate selection+funnel {sw.ElapsedMilliseconds:N0} ms");
    Console.WriteLine($"   SizeCandidates={result.SizeCandidates:N0}  PartialHashed={result.PartialHashed:N0}  " +
                      $"FullHashed={result.FullHashed:N0}  (unique sizes => no content read)");
}

// ---------- C. hash throughput on real duplicate pairs ----------
void ScenarioHashThroughput(int pairs, int kb)
{
    var dir = Path.Combine(root, "hash");
    Directory.CreateDirectory(dir);
    var db = new Database(Path.Combine(root, "hash.db"));
    db.Initialize();
    var volumes = new VolumeRepository(db);
    var files = new FileRepository(db);
    var hash = new HashService();
    var grouper = new DuplicateGrouper(files, new GroupRepository(db), new LshRepository(db), hash);
    var vol = new VolumeRecord { Serial = "BENCHH", MountPath = dir, IsConnected = true };
    vol.Id = volumes.Upsert(vol);

    int size = kb * 1024;
    var rng = new Random(123);
    var batch = new List<FileRecord>();
    for (int p = 0; p < pairs; p++)
    {
        var data = new byte[size];
        rng.NextBytes(data);
        foreach (var tag in new[] { "a", "b" }) // две идентичные копии => дубль-пара
        {
            var name = $"{p}_{tag}.bin";
            File.WriteAllBytes(Path.Combine(dir, name), data);
            batch.Add(new FileRecord { VolumeId = vol.Id, RelPath = name, Name = name, Size = size,
                Mtime = DateTimeOffset.UnixEpoch, Category = FileCategory.Other, ScanState = ScanState.Meta });
        }
    }
    files.UpsertMetaBatch(batch);

    var rep = new TimingReporter();
    var req = new ExactScanRequest
    {
        MountById = new Dictionary<long, string> { [vol.Id] = dir },
        VolumesById = new Dictionary<long, VolumeRecord> { [vol.Id] = vol },
        InScope = _ => true, ReadPlans = new Dictionary<long, VolumeReadPlan>(),
        MaxVolumeParallelism = 1, RequestedParallelism = Environment.ProcessorCount, Rule = RepresentativeRule.Oldest
    };
    var engine = new ExactDuplicateEngineV2(files, hash, grouper);
    var sw = Stopwatch.StartNew();
    var result = engine.ScanExact(req, rep, CancellationToken.None);
    sw.Stop();

    double mb = rep.Bytes / (1024.0 * 1024);
    Console.WriteLine($"C. HASH       {pairs * 2:N0} files x {kb} KB ({pairs:N0} dup pairs) total {sw.ElapsedMilliseconds:N0} ms");
    Console.WriteLine($"   partial stage {rep.PartialMs:N0} ms; full stage {rep.FullMs:N0} ms; " +
                      $"content read {mb:N0} MB => {mb / Math.Max(0.001, (rep.PartialMs + rep.FullMs) / 1000.0):N0} MB/sec");
    Console.WriteLine($"   PartialHashed={result.PartialHashed:N0}  FullHashed={result.FullHashed:N0}  GroupsCreated={result.GroupsCreated:N0}");
}

int Arg(int i, int def) => args.Length > i && int.TryParse(args[i], out var v) ? v : def;

// Репортёр, замеряющий длительность стадий и прочитанные байты.
sealed class TimingReporter : IExactScanReporter
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _partialStart = -1, _fullStart = -1, _groupingStart = -1;
    public long Bytes;
    public long PartialMs => _fullStart >= 0 && _partialStart >= 0 ? _fullStart - _partialStart : 0;
    public long FullMs => _groupingStart >= 0 && _fullStart >= 0 ? _groupingStart - _fullStart : 0;

    public void WaitGate() { }
    public void Stage(ScanStage s)
    {
        long t = _sw.ElapsedMilliseconds;
        if (s == ScanStage.PartialHashing) _partialStart = t;
        else if (s == ScanStage.FullHashing && _fullStart < 0) _fullStart = t;
        else if (s == ScanStage.Grouping) _groupingStart = t;
    }
    public void StageQuiet(ScanStage s) { if (s == ScanStage.FullHashing && _fullStart < 0) _fullStart = _sw.ElapsedMilliseconds; }
    public void FileHashed(string path, long bytes) => Interlocked.Add(ref Bytes, bytes);
    public void Skipped() { }
    public void ChangedSkipped() { }
    public void Fraction(double v) { }
    public void ExactGroups(long c) { }
    public void SessionTick() { }
}
