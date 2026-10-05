using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

/// <summary>Регрессионные тесты исправлений (в т.ч. hardlink-aware reclaimable и дробление LSH).</summary>
public class FixesTests : IDisposable
{
    private readonly string _dir;

    public FixesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_fix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // ---- M2: очистка осиротевших жёстких ссылок ----

    [Fact]
    public void CleanupTempFiles_removes_hardlink_orphans()
    {
        File.WriteAllText(Path.Combine(_dir, "x.bin" + SafeIo.HardLinkSuffix), "junk");
        File.WriteAllText(Path.Combine(_dir, "y.bin" + SafeIo.TempSuffix), "junk");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "keep");

        int removed = SafeIo.CleanupTempFiles(_dir);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(_dir, "x.bin" + SafeIo.HardLinkSuffix)));
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));
    }

    // ---- M1: упреждающая запись в журнал + откат ----

    [Fact]
    public void OperationLog_Delete_removes_entry()
    {
        var db = new Database(Path.Combine(_dir, "log.db"));
        db.Initialize();
        var log = new OperationLogRepository(db);

        var id = log.Insert(new OperationLogEntry
        {
            OpType = OperationType.MoveToQuarantine,
            SrcPath = "a", DstPath = "b",
            PerformedAt = DateTimeOffset.Now, Reversible = true
        });
        Assert.NotNull(log.GetById(id));

        log.Delete(id);
        Assert.Null(log.GetById(id));
    }

    [Fact]
    public void Delete_to_quarantine_logs_entry_and_can_restore()
    {
        var db = new Database(Path.Combine(_dir, "q.db"));
        db.Initialize();
        var log = new OperationLogRepository(db);
        var ops = new FileOpsService(log, new FileRepository(db), new GroupRepository(db),
            new HashService(), Path.Combine(_dir, "quarantine"));

        var src = Path.Combine(_dir, "file.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4, 5 });
        var rec = new FileRecord { VolumeId = 1, RelPath = "file.bin", Name = "file.bin", Size = 5 };

        bool ok = ops.Delete(rec, src, useRecycleBin: false, groupId: null);

        Assert.True(ok);
        Assert.False(File.Exists(src)); // перемещён в карантин

        var entries = log.GetAll(onlyReversible: true);
        Assert.Single(entries);
        Assert.True(File.Exists(entries[0].DstPath!)); // файл реально в карантине

        bool restored = ops.Restore(entries[0]);
        Assert.True(restored);
        Assert.True(File.Exists(src)); // восстановлен на место
    }

    // ---- Настройка «папка удалённых файлов»: Reconfigure меняет карантин на лету ----

    [Fact]
    public void Reconfigure_redirects_new_deletes_to_chosen_folder_and_old_restores_still_work()
    {
        var db = new Database(Path.Combine(_dir, "qr.db"));
        db.Initialize();
        var log = new OperationLogRepository(db);
        var oldQuarantine = Path.Combine(_dir, "quarantine_old");
        var ops = new FileOpsService(log, new FileRepository(db), new GroupRepository(db),
            new HashService(), oldQuarantine);

        // Удаление №1 идёт в стандартную папку.
        var src1 = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(src1, new byte[] { 1, 2, 3 });
        Assert.True(ops.Delete(new FileRecord { VolumeId = 1, RelPath = "a.bin", Name = "a.bin", Size = 3 },
            src1, useRecycleBin: false, groupId: null));
        var firstEntry = log.GetAll(onlyReversible: true).Single();
        Assert.StartsWith(oldQuarantine, firstEntry.DstPath!, StringComparison.OrdinalIgnoreCase);

        // Пользователь выбрал другую папку — применяем без перезапуска.
        var newQuarantine = Path.Combine(_dir, "Удалённые");
        ops.Reconfigure(newQuarantine);
        Assert.Equal(newQuarantine, ops.QuarantineDir);
        Assert.True(Directory.Exists(newQuarantine)); // папка создана сразу

        // Удаление №2 идёт уже в новую папку.
        var src2 = Path.Combine(_dir, "b.bin");
        File.WriteAllBytes(src2, new byte[] { 4, 5, 6, 7 });
        Assert.True(ops.Delete(new FileRecord { VolumeId = 1, RelPath = "b.bin", Name = "b.bin", Size = 4 },
            src2, useRecycleBin: false, groupId: null));
        var secondEntry = log.GetAll(onlyReversible: true)
            .First(e => e.FileName == "b.bin");
        Assert.StartsWith(newQuarantine, secondEntry.DstPath!, StringComparison.OrdinalIgnoreCase);

        // Старый файл из прежней папки всё ещё восстанавливается (путь в журнале абсолютный).
        Assert.True(ops.Restore(firstEntry));
        Assert.True(File.Exists(src1));

        // Пустое значение игнорируется (папка не меняется).
        ops.Reconfigure("   ");
        Assert.Equal(newQuarantine, ops.QuarantineDir);
    }

    // ---- M2: жёсткая ссылка не оставляет временный файл ----

    [Fact]
    public void Hardlink_leaves_no_temp_file()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)) return;

        var data = new byte[4096];
        new Random(7).NextBytes(data);
        var rep = Path.Combine(_dir, "rep.bin");
        var dup = Path.Combine(_dir, "dup.bin");
        File.WriteAllBytes(rep, data);
        File.WriteAllBytes(dup, (byte[])data.Clone());

        var db = new Database(Path.Combine(_dir, "hl.db"));
        db.Initialize();
        var ops = new FileOpsService(new OperationLogRepository(db), new FileRepository(db),
            new GroupRepository(db), new HashService(), Path.Combine(_dir, "q"));

        bool ok = ops.ReplaceWithHardLink(new FileRecord { Name = "dup.bin", Size = data.Length }, dup, rep, null);

        Assert.True(ok);
        Assert.True(File.Exists(dup));
        Assert.Empty(Directory.GetFiles(_dir, "*" + SafeIo.HardLinkSuffix, SearchOption.AllDirectories));
    }

    // ---- H2: крупные корзины LSH не отбрасываются (нет молчаливого пропуска) ----

    [Fact]
    public void UnionSimilarInBucket_finds_near_dup_in_oversized_bucket()
    {
        var hashOf = new Dictionary<long, ulong>();
        var ids = new List<long>();

        // Близкая пара: отличается на 1 бит в band7, совпадает в band0..6.
        hashOf[1] = 0UL; ids.Add(1);
        hashOf[2] = 1UL << 56; ids.Add(2);

        // 40 наполнителей: band0 = 0 (та же корзина), но band1 различается → при дроблении уходят.
        long id = 100;
        for (int i = 1; i <= 40; i++) { hashOf[id] = ((ulong)i) << 8; ids.Add(id); id++; }

        var dsu = new Dsu();
        foreach (var x in ids) dsu.MakeSet(x);

        // pairCap=16 < 42 → принудительно срабатывает дробление по полосам.
        DuplicateGrouper.UnionSimilarInBucket(ids, pairCap: 16,
            (a, b) => PerceptualLsh.Similarity(hashOf[a], hashOf[b]) >= 0.9,
            (i, level) => PerceptualLsh.BandKey(hashOf[i], level), PerceptualLsh.Bands, dsu);

        Assert.Equal(dsu.Find(1), dsu.Find(2));      // близкая пара найдена несмотря на размер корзины
        Assert.NotEqual(dsu.Find(1), dsu.Find(100)); // далёкий наполнитель не объединён
    }

    [Fact]
    public void UnionSimilarInBucket_small_bucket_unions_only_similar()
    {
        var hashOf = new Dictionary<long, ulong> { [1] = 0UL, [2] = 3UL, [3] = 0xFFFFFFFFUL };
        var ids = new List<long> { 1, 2, 3 };
        var dsu = new Dsu();
        foreach (var x in ids) dsu.MakeSet(x);

        DuplicateGrouper.UnionSimilarInBucket(ids, pairCap: 100,
            (a, b) => PerceptualLsh.Similarity(hashOf[a], hashOf[b]) >= 0.9,
            (i, level) => PerceptualLsh.BandKey(hashOf[i], level), PerceptualLsh.Bands, dsu);

        Assert.Equal(dsu.Find(1), dsu.Find(2));      // Хэмминг 2 → похожи
        Assert.NotEqual(dsu.Find(1), dsu.Find(3));   // Хэмминг 32 → не похожи
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
