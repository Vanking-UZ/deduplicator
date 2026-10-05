using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Потоковый движок точных дубликатов (ExactDuplicateEngineV2) и выборка кандидатов без N+1.
/// </summary>
public class Sprint2EngineTests : IDisposable
{
    private readonly string _dir;

    public Sprint2EngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(string name, byte[] data)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    private sealed class RecordingReporter : IExactScanReporter
    {
        public int Hashed;
        public readonly List<ScanStage> Stages = new();
        public long Groups;
        public void WaitGate() { }
        public void Stage(ScanStage s) => Stages.Add(s);
        public void StageQuiet(ScanStage s) => Stages.Add(s);
        public void FileHashed(string p, long b) => Interlocked.Increment(ref Hashed);
        public void Skipped() { }
        public void ChangedSkipped() { }
        public void Fraction(double v) { }
        public void ExactGroups(long c) => Groups = c;
        public void SessionTick() { }
    }

    [Fact]
    public void Engine_does_not_read_unique_size_files_and_groups_only_exact_duplicates()
    {
        // A/A2: одинаковые (один размер S1) → группа. B/C: один размер S2, но РАЗНОЕ содержимое →
        // частичный хэш есть, полного нет, группы нет. U: уникальный размер → НЕ читается вовсе.
        var a = new byte[100_000]; new Random(1).NextBytes(a);
        Write("a.bin", a);
        Write("a2.bin", (byte[])a.Clone());
        var b = new byte[50_000]; new Random(2).NextBytes(b);
        var c = new byte[50_000]; new Random(3).NextBytes(c);
        Write("b.bin", b);
        Write("c.bin", c);
        Write("u.bin", new byte[12_345]); // уникальный размер

        var db = new Database(Path.Combine(_dir, "s2.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();

        var vol = new VolumeRecord { Serial = "S2VOL", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        // Только метаданные (как после этапа перечисления): хэши пока null.
        var records = Directory.GetFiles(_dir, "*.bin").Select(p => new FileRecord
        {
            VolumeId = vol.Id,
            RelPath = Path.GetFileName(p),
            Name = Path.GetFileName(p),
            Ext = ".bin",
            Size = new FileInfo(p).Length,
            Mtime = DateTimeOffset.Now,
            Category = FileCategory.Other,
            ScanState = ScanState.Meta
        }).ToList();
        files.UpsertMetaBatch(records);

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var engine = new ExactDuplicateEngineV2(files, hash, grouper);
        var reporter = new RecordingReporter();

        var request = new ExactScanRequest
        {
            MountById = new Dictionary<long, string> { [vol.Id] = _dir },
            VolumesById = new Dictionary<long, VolumeRecord> { [vol.Id] = vol },
            InScope = _ => true,
            ReadPlans = new Dictionary<long, VolumeReadPlan>(),
            MaxVolumeParallelism = 1,
            RequestedParallelism = 1,
            Rule = RepresentativeRule.Oldest
        };

        var result = engine.ScanExact(request, reporter, default);

        Assert.Equal(1, result.GroupsCreated);     // только A/A2
        Assert.Equal(4, result.SizeCandidates);    // A,A2,B,C (U исключён по уникальному размеру)
        Assert.Equal(1, reporter.Groups);
        Assert.Contains(ScanStage.PartialHashing, reporter.Stages);
        Assert.Contains(ScanStage.Grouping, reporter.Stages);

        // U с уникальным размером не читался — частичный хэш отсутствует.
        FileRecord ById(string name) => files.GetByVolumeAndPath(vol.Id, name)!;
        Assert.Null(ById("u.bin").PartialHash);
        Assert.Null(ById("u.bin").FullHash);

        // A/A2 полностью прохэшированы и сгруппированы.
        Assert.NotNull(ById("a.bin").FullHash);
        Assert.NotNull(ById("a2.bin").FullHash);

        // B/C: частичный есть (кандидаты по размеру), полного нет (частичный различается).
        Assert.NotNull(ById("b.bin").PartialHash);
        Assert.Null(ById("b.bin").FullHash);

        var exact = groups.GetGroups(MatchType.Exact);
        Assert.Single(exact);
        Assert.Equal(2, exact[0].Members.Count);
    }

    [Fact]
    public void Candidate_query_returns_only_dup_sizes_ordered_no_n_plus_1()
    {
        var db = new Database(Path.Combine(_dir, "stream.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var vol = new VolumeRecord { Serial = "STR", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        // размеры: 30(×3), 10(×2) — дубликаты; 20, 99 — уникальны (должны быть исключены).
        var sizes = new long[] { 30, 20, 10, 30, 99, 10, 30 };
        var recs = sizes.Select((s, i) => new FileRecord
        {
            VolumeId = vol.Id, RelPath = $"f{i}.bin", Name = $"f{i}.bin",
            Size = s, Mtime = DateTimeOffset.Now, Category = FileCategory.Other, ScanState = ScanState.Meta
        }).ToList();
        files.UpsertMetaBatch(recs);

        // Шаг 1: дубль-размеры одним запросом (без N+1) — только 10 и 30.
        var dupSizes = files.GetDuplicateSizeCandidates();
        Assert.Equal(new long[] { 10, 30 }, dupSizes.OrderBy(x => x).ToArray());

        // Шаг 2: файлы этих размеров одним запросом на пачку, упорядоченные по размеру.
        var rows = files.GetBySizesOrdered(dupSizes);
        Assert.Equal(5, rows.Count);
        Assert.All(rows, f => Assert.Contains(f.Size, new long[] { 10, 30 }));
        var ordered = rows.Select(f => f.Size).ToList();
        Assert.Equal(ordered.OrderBy(x => x).ToList(), ordered);
        Assert.Equal(2, rows.Count(f => f.Size == 10));
        Assert.Equal(3, rows.Count(f => f.Size == 30));
    }

    [Fact]
    public void GlobalCap_uses_sum_of_volume_dop_but_stays_bounded()
    {
        // Два тома: бюджет — СУММА (а не максимум одного). На любой машине ≥1 ядра: min(2, 2×ядер)=2.
        Assert.Equal(2, VolumeParallelExecutor.GlobalCap(new[] { 1, 1 }));

        // Один том — ровно его DOP (sum == maxSingle ≤ потолок).
        Assert.Equal(5, VolumeParallelExecutor.GlobalCap(new[] { 5 }));

        // Самый «толстый» том всегда получает свой DOP, даже рядом с мелкими.
        Assert.True(VolumeParallelExecutor.GlobalCap(new[] { 100, 1, 1 }) >= 100);

        // Множество томов не взрывает бюджет: ограничен 2× ядер.
        int many = VolumeParallelExecutor.GlobalCap(Enumerable.Repeat(1, 1000).ToArray());
        Assert.Equal(Environment.ProcessorCount * 2, many);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
