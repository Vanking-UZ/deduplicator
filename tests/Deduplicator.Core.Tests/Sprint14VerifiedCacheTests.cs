using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Кэш побайтовой сверки verified_full_hash и пакетный lookup метаданных при
/// инкрементальном скане. Ключевая гарантия безопасности: кэш живёт только пока метаданные
/// строки строго переиспользованы — любое изменение файла (Upsert строки, пересчёт full_hash)
/// сбрасывает его, и сверка идёт чтением заново.
/// </summary>
public class Sprint14VerifiedCacheTests : IDisposable
{
    private readonly string _root;

    public Sprint14VerifiedCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dedup_s14_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ---------------- Обёртка-счётчик ----------------

    /// <summary>Обёртка над реальным HashService: считает вызовы побайтовой сверки —
    /// по счётчику видно, читалось ли содержимое при группировке.</summary>
    private sealed class CountingHashService : IHashService
    {
        private readonly HashService _inner = new();
        private int _byteEqualCalls;

        public int ByteEqualCalls => Volatile.Read(ref _byteEqualCalls);
        public void Reset() => Interlocked.Exchange(ref _byteEqualCalls, 0);

        public byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default, bool preferWholeRead = false)
            => _inner.ComputePartialHash(fullPath, size, ct, preferWholeRead);

        public byte[] ComputeFullHash(string fullPath, CancellationToken ct = default)
            => _inner.ComputeFullHash(fullPath, ct);

        public byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default)
            => _inner.ComputeFullHash(fullPath, expectedSize, ct);

        public bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default, int blockBytes = 0)
        {
            Interlocked.Increment(ref _byteEqualCalls);
            return _inner.FilesAreByteEqual(pathA, pathB, ct, blockBytes);
        }
    }

    // ---------------- Сквозная обвязка (реальный ScanService, как в EndToEndTests) ----------------

    private sealed record Ctx(
        FileRepository Files, GroupRepository Groups, ScanService Scan, CountingHashService Hash,
        string Corpus, long VolumeId, string MountPath)
    {
        public string Rel(string fileName) =>
            Path.GetRelativePath(MountPath, Path.Combine(Corpus, fileName));

        public FileRecord Record(string fileName) =>
            Files.GetByVolumeAndPath(VolumeId, Rel(fileName))
            ?? throw new InvalidOperationException($"Нет записи для {fileName}");
    }

    private Ctx BuildContext(string name)
    {
        var corpus = Path.Combine(_root, name);
        Directory.CreateDirectory(corpus);

        var db = new Database(Path.Combine(_root, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, name + "_app")));
        var volSvc = new VolumeService(volumes);
        var hash = new CountingHashService();
        var images = new ImageMatchService(Path.Combine(_root, name + "_thumbs"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        // Счётчик — И в движке (через ScanService), И в группировщике: сверка идёт в DuplicateGrouper.
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings,
            new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));

        var catalog = volSvc.SyncAndGetCatalog();
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(corpus));
        var vol = catalog.First(v => v.IsConnected && v.MountPath is not null &&
            string.Equals(Path.GetPathRoot(v.MountPath), driveRoot, StringComparison.OrdinalIgnoreCase));

        return new Ctx(files, groups, scan, hash, corpus, vol.Id, vol.MountPath!);
    }

    /// <summary>Инкрементальный скан папки корпуса (точные дубли).</summary>
    private static async Task ScanAsync(Ctx ctx)
    {
        await ctx.Scan.RunAsync(new ScanOptions
        {
            Folders = { ctx.Corpus },
            Depth = ScanDepth.ExactOnly,
            Parallelism = 2,
            Incremental = true,
            SkipHiddenSystem = false
        }, default);
        Assert.Equal(ScanStage.Completed, ctx.Scan.Progress.Stage);
    }

    private static byte[] RandomBytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ---------------- 2.5 (а) + 2.7: повторный скан — без чтения содержимого ----------------

    [Fact]
    public async Task Repeat_scan_reuses_verified_cache_and_rows_without_reading_content()
    {
        var ctx = BuildContext("repeat");
        var data = RandomBytes(60_000, 1);
        File.WriteAllBytes(Path.Combine(ctx.Corpus, "dup1.bin"), data);
        File.WriteAllBytes(Path.Combine(ctx.Corpus, "dup2.bin"), (byte[])data.Clone());
        File.WriteAllBytes(Path.Combine(ctx.Corpus, "unique.bin"), RandomBytes(4_321, 2));

        // Скан 1: дубли сверяются чтением, кэш записывается.
        await ScanAsync(ctx);
        Assert.True(ctx.Hash.ByteEqualCalls >= 1, "первый скан обязан сверять чтением");

        var exact1 = ctx.Groups.GetGroups(MatchType.Exact);
        Assert.Single(exact1);
        Assert.Equal(2, exact1[0].Members.Count);
        Assert.Equal(1.0, exact1[0].Confidence); // байт-сверенная группа

        var d1 = ctx.Record("dup1.bin");
        var d2 = ctx.Record("dup2.bin");
        var uq = ctx.Record("unique.bin");
        Assert.NotNull(d1.FullHash);
        Assert.Equal(d1.FullHash, d1.VerifiedFullHash); // кэш = full_hash
        Assert.Equal(d2.FullHash, d2.VerifiedFullHash);
        Assert.Null(uq.VerifiedFullHash); // не участвовал в сверке — кэша нет

        // Скан 2 (те же файлы): строки переиспользованы (2.7), сверка — из кэша, ноль чтений.
        ctx.Hash.Reset();
        await ScanAsync(ctx);
        Assert.Equal(0, ctx.Hash.ByteEqualCalls); // неизменённые файлы не читаются повторно

        var exact2 = ctx.Groups.GetGroups(MatchType.Exact);
        Assert.Single(exact2);
        Assert.Equal(2, exact2[0].Members.Count);
        Assert.Equal(1.0, exact2[0].Confidence); // кэшированная сверка равноценна прочитанной

        // 2.7: id не изменились, хэши сохранены (строки реиспользованы, а не пересозданы).
        var d1b = ctx.Record("dup1.bin");
        var d2b = ctx.Record("dup2.bin");
        Assert.Equal(d1.Id, d1b.Id);
        Assert.Equal(d2.Id, d2b.Id);
        Assert.Equal(d1.FullHash, d1b.FullHash);
        Assert.Equal(d2.FullHash, d2b.FullHash);
        Assert.Equal(uq.Id, ctx.Record("unique.bin").Id);

        ctx.Scan.Dispose();
    }

    // ---------------- 2.5 (б): изменение файла сбрасывает кэш → сверка чтением заново ----------------

    [Fact]
    public async Task Modified_file_resets_cache_and_is_reverified_by_reading()
    {
        var ctx = BuildContext("modified");
        var data = RandomBytes(60_000, 3);
        var dup2Path = Path.Combine(ctx.Corpus, "dup2.bin");
        File.WriteAllBytes(Path.Combine(ctx.Corpus, "dup1.bin"), data);
        File.WriteAllBytes(dup2Path, (byte[])data.Clone());

        await ScanAsync(ctx);
        var d2Before = ctx.Record("dup2.bin");
        Assert.Equal(d2Before.FullHash, d2Before.VerifiedFullHash);

        // Перезапись ТЕМ ЖЕ содержимым того же размера, но с новым mtime: строгая тройная сверка
        // не пройдёт → строка попадёт в UpsertMetaBatch → кэш и хэши сброшены → перехэш + сверка чтением.
        File.WriteAllBytes(dup2Path, (byte[])data.Clone());
        File.SetLastWriteTimeUtc(dup2Path, DateTime.UtcNow.AddSeconds(7));

        ctx.Hash.Reset();
        await ScanAsync(ctx);
        Assert.True(ctx.Hash.ByteEqualCalls >= 1, "изменённый файл обязан сверяться чтением, а не кэшем");

        // Группа собрана заново: смешанный кластер «кэшированный dup1 + новый dup2» сверен чтением.
        var exact = ctx.Groups.GetGroups(MatchType.Exact);
        Assert.Single(exact);
        Assert.Equal(2, exact[0].Members.Count);
        Assert.Equal(1.0, exact[0].Confidence);

        // Строка та же (Upsert по volume_id+rel_path), файл перехэширован, кэш записан заново.
        var d2After = ctx.Record("dup2.bin");
        Assert.Equal(d2Before.Id, d2After.Id);
        Assert.NotNull(d2After.FullHash);
        Assert.Equal(d2After.FullHash, d2After.VerifiedFullHash);

        ctx.Scan.Dispose();
    }

    // ---------------- 2.7: изменённое содержимое перехэшируется, группа распадается ----------------

    [Fact]
    public async Task Changed_content_same_size_is_rehashed_and_leaves_group()
    {
        var ctx = BuildContext("changed");
        var data = RandomBytes(60_000, 4);
        var dup2Path = Path.Combine(ctx.Corpus, "dup2.bin");
        File.WriteAllBytes(Path.Combine(ctx.Corpus, "dup1.bin"), data);
        File.WriteAllBytes(dup2Path, (byte[])data.Clone());

        await ScanAsync(ctx);
        var d2Before = ctx.Record("dup2.bin");
        Assert.NotNull(d2Before.PartialHash);

        // Другое содержимое ТОГО ЖЕ размера + новый mtime: файл больше не дубликат.
        File.WriteAllBytes(dup2Path, RandomBytes(60_000, 5));
        File.SetLastWriteTimeUtc(dup2Path, DateTime.UtcNow.AddSeconds(7));

        await ScanAsync(ctx);

        Assert.Empty(ctx.Groups.GetGroups(MatchType.Exact)); // группы нет — содержимое разошлось

        var d2After = ctx.Record("dup2.bin");
        Assert.Equal(d2Before.Id, d2After.Id);                       // строка та же
        Assert.NotNull(d2After.PartialHash);                         // перехэширован...
        Assert.NotEqual(d2Before.PartialHash, d2After.PartialHash);  // ...новым содержимым
        Assert.Null(d2After.VerifiedFullHash);                       // кэш сверки сброшен

        // Нетронутый dup1 сохранил и строку, и валидный кэш (его содержимое не менялось).
        var d1After = ctx.Record("dup1.bin");
        Assert.Equal(d1After.FullHash, d1After.VerifiedFullHash);

        ctx.Scan.Dispose();
    }

    // ---------------- 2.5 (в): попадание строки в UpsertMetaBatch сбрасывает кэш ----------------

    [Fact]
    public void Upsert_meta_batch_always_resets_verified_cache()
    {
        var db = new Database(Path.Combine(_root, "upsert_reset.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S14-UP", Label = "t" });

        byte[] h = { 1, 2, 3, 4, 5 };
        var rec = new FileRecord
        {
            VolumeId = vid, RelPath = "a.bin", Name = "a.bin", Size = 5,
            Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now,
            Category = FileCategory.Other, FullHash = h
        };
        files.UpsertMetaBatch(new[] { rec });

        // Защитный фильтр: значение, не равное full_hash строки, не записывается вовсе.
        files.UpdateVerifiedFullHashBatch(new[] { (rec.Id, new byte[] { 9, 9, 9 }) });
        Assert.Null(files.GetById(rec.Id)!.VerifiedFullHash);

        files.UpdateVerifiedFullHashBatch(new[] { (rec.Id, h) });
        Assert.Equal(h, files.GetById(rec.Id)!.VerifiedFullHash);

        // Повторный Upsert той же строки — даже с НЕИЗМЕННЫМИ метаданными — сбрасывает кэш:
        // в UpsertMetaBatch строка попадает только когда строгое реиспользование не сработало.
        files.UpsertMetaBatch(new[] { rec });
        var after = files.GetById(rec.Id)!;
        Assert.Equal(h, after.FullHash);        // сам full_hash из записи сохранён
        Assert.Null(after.VerifiedFullHash);    // а кэш сверки обнулён
    }

    [Fact]
    public void Update_computed_batch_clears_verified_when_full_hash_changes()
    {
        var db = new Database(Path.Combine(_root, "computed_reset.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S14-CB", Label = "t" });

        byte[] h1 = { 1, 1, 1 };
        byte[] h2 = { 2, 2, 2 };
        var rec = new FileRecord
        {
            VolumeId = vid, RelPath = "b.bin", Name = "b.bin", Size = 3,
            Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now,
            Category = FileCategory.Other, FullHash = h1
        };
        files.UpsertMetaBatch(new[] { rec });
        files.UpdateVerifiedFullHashBatch(new[] { (rec.Id, h1) });
        Assert.Equal(h1, files.GetById(rec.Id)!.VerifiedFullHash);

        // Пересчёт full_hash (движок) → старый кэш относится к другому содержимому → NULL.
        var loaded = files.GetById(rec.Id)!;
        loaded.FullHash = h2;
        files.UpdateComputedBatch(new[] { loaded });
        Assert.Null(files.GetById(rec.Id)!.VerifiedFullHash);

        // А при НЕИЗМЕННОМ full_hash кэш переживает UpdateComputedBatch (перцептив и т.п.).
        files.UpdateVerifiedFullHashBatch(new[] { (rec.Id, h2) });
        var loaded2 = files.GetById(rec.Id)!;
        loaded2.PHash = new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 };
        files.UpdateComputedBatch(new[] { loaded2 });
        var after = files.GetById(rec.Id)!;
        Assert.Equal(h2, after.VerifiedFullHash);
        Assert.NotNull(after.PHash);
    }

    // ---------------- 2.7: пакетный lookup метаданных ----------------

    [Fact]
    public void GetExistingMetaByPaths_returns_only_requested_paths_and_chunks_large_batches()
    {
        var db = new Database(Path.Combine(_root, "lookup.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S14-LK", Label = "t" });
        long other = volumes.Upsert(new VolumeRecord { Serial = "S14-LK2", Label = "t2" });

        // 900 строк — больше двух чанков по 400 (проверяем разбиение IN-запроса).
        var created = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var mtime = new DateTimeOffset(2025, 6, 15, 8, 30, 0, TimeSpan.Zero);
        var recs = Enumerable.Range(0, 900).Select(i => new FileRecord
        {
            VolumeId = vid, RelPath = $"dir\\f{i:D4}.bin", Name = $"f{i:D4}.bin",
            Size = 1000 + i, Mtime = mtime.AddSeconds(i), Created = created,
            Category = FileCategory.Other
        }).ToList();
        files.UpsertMetaBatch(recs);
        // Файл ДРУГОГО тома с тем же путём не должен попадать в выборку.
        files.UpsertMetaBatch(new[] { new FileRecord
        {
            VolumeId = other, RelPath = "dir\\f0000.bin", Name = "f0000.bin",
            Size = 777, Mtime = mtime, Created = created, Category = FileCategory.Other
        }});

        var wanted = recs.Take(850).Select(r => r.RelPath).ToList();
        wanted.Add("dir\\missing.bin"); // отсутствующий путь просто не попадает в ответ

        var map = files.GetExistingMetaByPaths(vid, wanted);
        Assert.Equal(850, map.Count);

        var m0 = map["dir\\f0000.bin"];
        Assert.Equal(recs[0].Id, m0.Id);
        Assert.Equal(1000, m0.Size);        // размер своего тома, не 777 от чужого
        Assert.Equal(mtime, m0.Mtime);
        Assert.Equal(created, m0.Created);
        Assert.Equal(recs[849].Id, map["dir\\f0849.bin"].Id);
        Assert.False(map.ContainsKey("dir\\f0850.bin")); // не запрашивали — не вернулось
        Assert.False(map.ContainsKey("dir\\missing.bin"));

        Assert.Empty(files.GetExistingMetaByPaths(vid, Array.Empty<string>()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
