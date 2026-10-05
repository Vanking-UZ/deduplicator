using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Этап 2.1: батчинг хэширования (один HashByVolume на фазу для всей пачки размеров) +
/// LCN-сортировка чтений на томах со штрафом позиционирования. Проверяем эквивалентность групп
/// прежней по-корзинной обработке, экономию полных хэшей и чистый LCN-сортировщик.
/// </summary>
public class Sprint12EngineBatchingTests : IDisposable
{
    private readonly string _dir;

    public Sprint12EngineBatchingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s12_" + Guid.NewGuid().ToString("N"));
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
        public double LastFraction;
        public long Groups;
        public void WaitGate() { }
        public void Stage(ScanStage s) => Stages.Add(s);
        public void StageQuiet(ScanStage s) => Stages.Add(s);
        public void FileHashed(string p, long b) => Interlocked.Increment(ref Hashed);
        public void Skipped() { }
        public void ChangedSkipped() { }
        public void Fraction(double v) => LastFraction = v;
        public void ExactGroups(long c) => Groups = c;
        public void SessionTick() { }
    }

    /// <summary>Стандартная обвязка: БД + метаданные всех *.bin из каталога (хэшей ещё нет).</summary>
    private (FileRepository Files, GroupRepository Groups, ExactDuplicateEngineV2 Engine,
        ExactScanRequest Request, RecordingReporter Reporter, long VolumeId) Build(string dbName)
    {
        var db = new Database(Path.Combine(_dir, dbName));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();

        var vol = new VolumeRecord { Serial = "S12VOL", MountPath = _dir, IsConnected = true };
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
        return (files, groups, engine, request, new RecordingReporter(), vol.Id);
    }

    // === (а) эквивалентность: пакетная воронка даёт ТЕ ЖЕ группы (по составу путей) ===
    [Fact]
    public void Batched_funnel_produces_same_groups_by_path_composition()
    {
        // Корпус: 2 группы дублей + уникальные. n1/n2 — один размер, но разное содержимое
        // (группы быть не должно); u — уникальный размер (не читается вовсе).
        var g1 = new byte[70_000]; new Random(11).NextBytes(g1);
        Write("g1a.bin", g1);
        Write("g1b.bin", (byte[])g1.Clone());
        var g2 = new byte[90_000]; new Random(12).NextBytes(g2);
        Write("g2a.bin", g2);
        Write("g2b.bin", (byte[])g2.Clone());
        Write("g2c.bin", (byte[])g2.Clone());
        var n1 = new byte[40_000]; new Random(13).NextBytes(n1);
        var n2 = new byte[40_000]; new Random(14).NextBytes(n2);
        Write("n1.bin", n1);
        Write("n2.bin", n2);
        Write("u.bin", new byte[7_777]);

        var (files, groups, engine, request, reporter, volId) = Build("equiv.db");
        var result = engine.ScanExact(request, reporter, default);

        // Состав групп — по путям: ровно {g1a,g1b} и {g2a,g2b,g2c}.
        var actual = groups.GetGroups(MatchType.Exact)
            .Select(g => g.Members.Select(m => files.GetById(m.FileId)!.Name).OrderBy(n => n).ToArray())
            .OrderBy(g => g[0]).ToList();
        Assert.Equal(2, actual.Count);
        Assert.Equal(new[] { "g1a.bin", "g1b.bin" }, actual[0]);
        Assert.Equal(new[] { "g2a.bin", "g2b.bin", "g2c.bin" }, actual[1]);
        Assert.Equal(2, result.GroupsCreated);

        // Счётчики воронки: u исключён по уникальному размеру, остальные 7 — кандидаты.
        Assert.Equal(7, result.SizeCandidates);
        Assert.Equal(7, result.PartialHashed);
        Assert.Equal(1.0, reporter.LastFraction); // прогресс дошёл до конца пачки корзин
        Assert.Contains(ScanStage.PartialHashing, reporter.Stages);
        Assert.Contains(ScanStage.FullHashing, reporter.Stages);
        Assert.Contains(ScanStage.Grouping, reporter.Stages);

        FileRecord ById(string name) => files.GetByVolumeAndPath(volId, name)!;
        Assert.Null(ById("u.bin").PartialHash);   // уникальный размер не читался
        Assert.Null(ById("u.bin").FullHash);
        Assert.NotNull(ById("n1.bin").PartialHash); // кандидат по размеру — частичный есть
    }

    // === (б) полный хэш по-прежнему считается ТОЛЬКО для коллизий частичного ===
    [Fact]
    public void Unique_partial_hash_skips_full_hash_in_batched_funnel()
    {
        // Пара дублей (частичные совпадут → полный нужен) + пара «тот же размер, другое
        // содержимое» (частичные разойдутся → полный НЕ считается).
        var d = new byte[60_000]; new Random(21).NextBytes(d);
        Write("dup1.bin", d);
        Write("dup2.bin", (byte[])d.Clone());
        var x = new byte[35_000]; new Random(22).NextBytes(x);
        var y = new byte[35_000]; new Random(23).NextBytes(y);
        Write("x.bin", x);
        Write("y.bin", y);

        var (files, _, engine, request, reporter, volId) = Build("needfull.db");
        var result = engine.ScanExact(request, reporter, default);

        Assert.Equal(4, result.SizeCandidates);
        Assert.Equal(4, result.PartialHashed);
        Assert.Equal(2, result.FullHashed); // только dup1/dup2 — уникальный partial полный хэш не получает

        FileRecord ById(string name) => files.GetByVolumeAndPath(volId, name)!;
        Assert.NotNull(ById("dup1.bin").FullHash);
        Assert.NotNull(ById("dup2.bin").FullHash);
        Assert.NotNull(ById("x.bin").PartialHash);
        Assert.Null(ById("x.bin").FullHash);
        Assert.Null(ById("y.bin").FullHash);
    }

    // === (в) LCN-сортировщик: по возрастанию, null — в конец, стабильно ===
    [Fact]
    public void SortByFirstLcn_orders_ascending_nulls_last_stable()
    {
        var lcn = new Dictionary<string, long?>
        {
            ["a"] = 500, ["b"] = null, ["c"] = 10, ["d"] = null, ["e"] = 10, ["f"] = 0
        };
        var sorted = ExactDuplicateEngineV2.SortByFirstLcn(
            new[] { "a", "b", "c", "d", "e", "f" }, s => lcn[s]);
        // 0 → 10 (c раньше e: стабильность при равных LCN) → 500; без LCN — в конец в исходном порядке.
        Assert.Equal(new[] { "f", "c", "e", "a", "b", "d" }, sorted);

        // Все без LCN — исходный порядок не меняется.
        Assert.Equal(new[] { "z", "y", "x" },
            ExactDuplicateEngineV2.SortByFirstLcn(new[] { "z", "y", "x" }, _ => (long?)null));

        // Пусто и один элемент — без сюрпризов.
        Assert.Empty(ExactDuplicateEngineV2.SortByFirstLcn(Array.Empty<string>(), _ => (long?)0L));
        Assert.Equal(new[] { "solo" },
            ExactDuplicateEngineV2.SortByFirstLcn(new[] { "solo" }, _ => (long?)42L));
    }

    // === LCN-обёртка деградирует в null молча (админ не нужен, ошибки не всплывают) ===
    [Fact]
    public void FirstLcn_wrapper_never_throws_and_degrades_to_null()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Нерезидентный файл (в MFT не поместится): на NTFS даст LCN ≥ 0, на другой ФС — null.
        var p = Write("lcn_probe.bin", new byte[128 * 1024]);
        var lcn = FastDedup.Engine.FileLayoutReader.TryGetFirstLcn(p);
        Assert.True(lcn is null || lcn >= 0);

        // Несуществующий путь — null, без исключения.
        Assert.Null(FastDedup.Engine.FileLayoutReader.TryGetFirstLcn(Path.Combine(_dir, "missing.bin")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
