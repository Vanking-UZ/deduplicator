using System.IO.Compression;
using System.Text.Json;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Переносимый кэш проверки на носителе (.dedupindex). Флешка, проверенная на
/// одном компьютере, на другом сканируется без пересчёта хэшей. Ключевые гарантии: кэш применяется
/// только при СТРОГОМ совпадении серийника/версий алгоритмов/метаданных файла; чужой/битый/
/// устаревший файл молча отклоняется; verified_full_hash (побайтовая сверка чужой машины)
/// не переносится никогда.
/// </summary>
public class Sprint22PortableIndexTests : IDisposable
{
    private readonly string _root;

    public Sprint22PortableIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dedup_s22_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ---------------- Обвязка ----------------

    /// <summary>Подменный каталог томов: ровно один «съёмный носитель» с заданными корнем и
    /// серийником. Позволяет прогнать РЕАЛЬНЫЙ ScanService по временной папке как по флешке:
    /// экспорт пишет .dedupindex в её корень, а не в корень настоящего диска.</summary>
    private sealed class FakeRemovableVolumeService : IVolumeService
    {
        private readonly VolumeRepository _repo;
        private readonly string _mountRoot;
        private readonly string _serial;

        public FakeRemovableVolumeService(VolumeRepository repo, string mountRoot, string serial)
        {
            _repo = repo;
            _mountRoot = mountRoot;
            _serial = serial;
        }

        public List<VolumeRecord> GetConnectedVolumes() => SyncAndGetCatalog();

        public List<VolumeRecord> SyncAndGetCatalog()
        {
            long id = _repo.Upsert(new VolumeRecord
            {
                Serial = _serial, Label = "Флешка", FsType = "exFAT", TotalBytes = 1L << 30
            });
            var v = _repo.GetById(id)!;
            v.IsConnected = true;
            v.MountPath = _mountRoot;
            v.IsRemovable = true;
            return new List<VolumeRecord> { v };
        }

        public long Register(VolumeRecord volume) => _repo.Upsert(volume);
    }

    /// <summary>Обёртка над реальным HashService: запоминает, ЧТО хэшировалось чтением, — по спискам
    /// путей видно, какие файлы переиспользованы из кэша (не читались), а какие пересчитаны.</summary>
    private sealed class RecordingHashService : IHashService
    {
        private readonly HashService _inner = new();
        private readonly object _gate = new();
        private int _byteEqualCalls;

        public int ByteEqualCalls => Volatile.Read(ref _byteEqualCalls);
        public List<string> PartialPaths { get; } = new();
        public List<string> FullPaths { get; } = new();

        public byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default, bool preferWholeRead = false)
        {
            lock (_gate) PartialPaths.Add(fullPath);
            return _inner.ComputePartialHash(fullPath, size, ct, preferWholeRead);
        }

        public byte[] ComputeFullHash(string fullPath, CancellationToken ct = default)
        {
            lock (_gate) FullPaths.Add(fullPath);
            return _inner.ComputeFullHash(fullPath, ct);
        }

        public byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default)
        {
            lock (_gate) FullPaths.Add(fullPath);
            return _inner.ComputeFullHash(fullPath, expectedSize, ct);
        }

        public bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default, int blockBytes = 0)
        {
            Interlocked.Increment(ref _byteEqualCalls);
            return _inner.FilesAreByteEqual(pathA, pathB, ct, blockBytes);
        }
    }

    /// <summary>«Компьютер»: своя БД/настройки + общий носитель (mountRoot). Эмуляция переноса
    /// флешки = два Machine с одним mountRoot и серийником, но разными БД.</summary>
    private sealed record Machine(
        FileRepository Files, GroupRepository Groups, VolumeRepository Volumes, ScanService Scan,
        RecordingHashService Hash, PortableIndexService Portable, long VolumeId, string MountRoot)
    {
        public FileRecord Record(string relPath) =>
            Files.GetByVolumeAndPath(VolumeId, relPath)
            ?? throw new InvalidOperationException($"Нет записи для {relPath}");
    }

    private Machine BuildMachine(string name, string mountRoot, string serial)
    {
        var db = new Database(Path.Combine(_root, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, name + "_app")));
        var volSvc = new FakeRemovableVolumeService(volumes, mountRoot, serial);
        var hash = new RecordingHashService();
        var images = new ImageMatchService(Path.Combine(_root, name + "_thumbs"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings,
            new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));
        long vid = volSvc.SyncAndGetCatalog()[0].Id;
        return new Machine(files, groups, volumes, scan, hash, new PortableIndexService(volumes, files), vid, mountRoot);
    }

    /// <summary>Скан «всего носителя» (как выбор тома в UI): инкрементально, точные дубли.</summary>
    private static async Task ScanAsync(Machine m)
    {
        await m.Scan.RunAsync(new ScanOptions
        {
            VolumeIds = { m.VolumeId },
            Depth = ScanDepth.ExactOnly,
            Parallelism = 2,
            Incremental = true,
            SkipHiddenSystem = false
        }, default);
        Assert.Equal(ScanStage.Completed, m.Scan.Progress.Stage);
    }

    private static byte[] RandomBytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static PortableIndexDocument ReadIndex(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<PortableIndexDocument>(gz)!;
    }

    private static void WriteIndex(string path, PortableIndexDocument doc)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gz, doc);
    }

    private string NewMount(string name)
    {
        var mount = Path.Combine(_root, name);
        Directory.CreateDirectory(mount);
        return mount;
    }

    // ---------------- (а) Экспорт: файл, gzip-json, заголовок, только строки с хэшами ----------------

    [Fact]
    public void Export_writes_valid_gzip_json_with_header_and_only_hashed_rows()
    {
        var mount = NewMount("usb_a");
        var db = new Database(Path.Combine(_root, "exp.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "USB-A1", Label = "Флешка" });

        var mtime = new DateTimeOffset(2025, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var hashed = new FileRecord
        {
            VolumeId = vid, RelPath = "photos\\a.jpg", Name = "a.jpg", Ext = ".jpg",
            Size = 100, Mtime = mtime, Created = created, Category = FileCategory.Image,
            PartialHash = new byte[] { 1, 2, 3 }, FullHash = new byte[] { 4, 5, 6 },
            PHash = new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 }, Sharpness = 12.5,
            PartialVer = HashService.CurrentPartialVersion, PHashVer = ImageMatchService.CurrentPHashVersion
        };
        var bare = new FileRecord
        {
            VolumeId = vid, RelPath = "docs\\b.txt", Name = "b.txt", Ext = ".txt",
            Size = 50, Mtime = mtime, Created = created, Category = FileCategory.Document
        };
        files.UpsertMetaBatch(new[] { hashed, bare });
        files.UpdateVerifiedFullHashBatch(new[] { (hashed.Id, new byte[] { 4, 5, 6 }) });

        var svc = new PortableIndexService(volumes, files);
        Assert.True(svc.TryExport(vid, mount));

        var path = Path.Combine(mount, PortableIndexService.FileName);
        Assert.True(File.Exists(path));
        // Скрытый+системный, чтобы не мозолить глаза в проводнике (best-effort, но на NTFS обязан пройти).
        var attrs = File.GetAttributes(path);
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        Assert.True(attrs.HasFlag(FileAttributes.System));

        var doc = ReadIndex(path);
        Assert.NotNull(doc.Header);
        Assert.Equal(PortableIndexService.FormatVersion, doc.Header!.FormatVersion);
        Assert.Equal("USB-A1", doc.Header.VolumeSerial);
        Assert.Equal(HashService.CurrentPartialVersion, doc.Header.PartialVer);
        Assert.Equal(ImageMatchService.CurrentPHashVersion, doc.Header.PHashVer);
        Assert.False(string.IsNullOrWhiteSpace(doc.Header.AppVersion));

        // Только строка с хэшами; безхэшевая docs\b.txt в кэш не попадает (бесполезна).
        var e = Assert.Single(doc.Entries!);
        Assert.Equal("photos\\a.jpg", e.RelPath);
        Assert.Equal(100, e.Size);
        Assert.Equal(mtime.UtcTicks, e.MtimeTicksUtc);
        Assert.Equal(created.UtcTicks, e.CreatedTicksUtc);
        Assert.Equal(new byte[] { 1, 2, 3 }, e.PartialHash);
        Assert.Equal(new byte[] { 4, 5, 6 }, e.FullHash);
        Assert.Equal(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 }, e.PHash);
        Assert.Equal(12.5, e.Sharpness);
        Assert.Equal(new byte[] { 4, 5, 6 }, e.VerifiedFullHash); // в ФАЙЛЕ есть; импорт его игнорирует

        // Повторный экспорт перезаписывает скрытый файл без ошибок (атомарная замена).
        Assert.True(svc.TryExport(vid, mount));

        // Том без строк с хэшами → экспорт честно пропускается.
        long emptyVid = volumes.Upsert(new VolumeRecord { Serial = "USB-EMPTY", Label = "t" });
        var emptyMount = NewMount("usb_a_empty");
        Assert.False(svc.TryExport(emptyVid, emptyMount));
        Assert.False(File.Exists(Path.Combine(emptyMount, PortableIndexService.FileName)));
    }

    // ---------------- (б) Round-trip: другая машина переиспользует хэши без пересчёта ----------------

    [Fact]
    public async Task Round_trip_on_new_machine_reuses_hashes_without_rehashing()
    {
        var mount = NewMount("usb_rt");
        var data = RandomBytes(60_000, 1);
        File.WriteAllBytes(Path.Combine(mount, "dup1.bin"), data);
        File.WriteAllBytes(Path.Combine(mount, "dup2.bin"), (byte[])data.Clone());
        File.WriteAllBytes(Path.Combine(mount, "unique.bin"), RandomBytes(4_321, 2));

        // «Компьютер A»: скан носителя; экспорт кэша происходит автоматически (том съёмный).
        var a = BuildMachine("rt_a", mount, "USB-RT");
        await ScanAsync(a);
        Assert.True(File.Exists(Path.Combine(mount, PortableIndexService.FileName)),
            "после успешного скана съёмного тома кэш должен быть выгружен автоматически");

        var aDup1 = a.Record("dup1.bin");
        var aDup2 = a.Record("dup2.bin");
        Assert.NotNull(aDup1.FullHash);

        // «Компьютер B»: НОВАЯ пустая БД, тот же носитель и серийник.
        var b = BuildMachine("rt_b", mount, "USB-RT");
        await ScanAsync(b);

        // Содержимое НЕ хэшировалось заново (ни частично, ни полностью)...
        Assert.Empty(b.Hash.PartialPaths);
        Assert.Empty(b.Hash.FullPaths);
        // ...а побайтовая сверка (страховка перед группировкой) выполнялась чтением как обычно.
        Assert.True(b.Hash.ByteEqualCalls >= 1, "byte-verify обязан работать и при кэшированных хэшах");

        // Группы те же, что на машине A: одна точная группа из двух файлов, сверенная чтением.
        var groups = b.Groups.GetGroups(MatchType.Exact);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Members.Count);
        Assert.Equal(1.0, groups[0].Confidence);

        // Хэши совпали со старыми — переиспользованы из кэша, версии актуальные.
        var bDup1 = b.Record("dup1.bin");
        var bDup2 = b.Record("dup2.bin");
        Assert.Equal(aDup1.PartialHash, bDup1.PartialHash);
        Assert.Equal(aDup1.FullHash, bDup1.FullHash);
        Assert.Equal(aDup2.PartialHash, bDup2.PartialHash);
        Assert.Equal(aDup2.FullHash, bDup2.FullHash);
        Assert.Equal(HashService.CurrentPartialVersion, bDup1.PartialVer);

        // Пользователь видит уведомление о переиспользовании (счётчик > 0).
        Assert.Contains("Найден кэш проверки на носителе", b.Scan.Progress.Message ?? "");

        a.Scan.Dispose();
        b.Scan.Dispose();
    }

    // ---------------- (б-2) VerifiedFullHash чужой машины не переносится ----------------

    [Fact]
    public async Task Imported_rows_do_not_inherit_verified_full_hash()
    {
        var mount = NewMount("usb_vf");
        var data = RandomBytes(60_000, 3);
        File.WriteAllBytes(Path.Combine(mount, "dup1.bin"), data);
        File.WriteAllBytes(Path.Combine(mount, "dup2.bin"), (byte[])data.Clone());

        var a = BuildMachine("vf_a", mount, "USB-VF");
        await ScanAsync(a);
        var aDup1 = a.Record("dup1.bin");
        Assert.Equal(aDup1.FullHash, aDup1.VerifiedFullHash); // машина A сверила чтением

        // В файле кэша сверка машины A ЕСТЬ (формат её хранит)...
        var doc = ReadIndex(Path.Combine(mount, PortableIndexService.FileName));
        Assert.NotNull(doc.Entries!.Single(e => e.RelPath == "dup1.bin").VerifiedFullHash);

        // На «машине B» дубликата больше нет: dup2 удалён — движку нечего сверять, и строка dup1
        // остаётся ровно с тем, что пришло из кэша при импорте.
        File.Delete(Path.Combine(mount, "dup2.bin"));
        var b = BuildMachine("vf_b", mount, "USB-VF");
        await ScanAsync(b);

        var bDup1 = b.Record("dup1.bin");
        Assert.Equal(aDup1.FullHash, bDup1.FullHash);    // хэш переиспользован из кэша...
        Assert.Null(bDup1.VerifiedFullHash);             // ...а сверка ЧУЖОЙ машины — нет
        Assert.Empty(b.Hash.PartialPaths);               // и ничего не пересчитывалось

        a.Scan.Dispose();
        b.Scan.Dispose();
    }

    // ---------------- (в) Чужой серийник → импорт отклонён ----------------

    [Fact]
    public async Task Import_rejects_foreign_volume_serial()
    {
        var mount = NewMount("usb_fs");
        var data = RandomBytes(60_000, 4);
        File.WriteAllBytes(Path.Combine(mount, "dup1.bin"), data);
        File.WriteAllBytes(Path.Combine(mount, "dup2.bin"), (byte[])data.Clone());

        var a = BuildMachine("fs_a", mount, "USB-OWN");
        await ScanAsync(a);
        Assert.True(File.Exists(Path.Combine(mount, PortableIndexService.FileName)));

        // «Машина B» видит носитель с ДРУГИМ серийником — кэш чужой, не применяется.
        var b = BuildMachine("fs_b", mount, "USB-ALIEN");
        Assert.Null(b.Portable.TryImport(b.VolumeId, mount));

        // И при скане хэши честно считаются заново.
        await ScanAsync(b);
        Assert.NotEmpty(b.Hash.PartialPaths);
        Assert.DoesNotContain("Найден кэш проверки", b.Scan.Progress.Message ?? "");

        a.Scan.Dispose();
        b.Scan.Dispose();
    }

    // ---------------- (г) Устаревшие версии алгоритмов → импорт отклонён ----------------

    [Fact]
    public void Import_rejects_stale_or_foreign_versions()
    {
        var mount = NewMount("usb_ver");
        var db = new Database(Path.Combine(_root, "ver.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "USB-VER", Label = "t" });
        files.UpsertMetaBatch(new[] { new FileRecord
        {
            VolumeId = vid, RelPath = "a.bin", Name = "a.bin", Size = 10,
            Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now,
            Category = FileCategory.Other, PartialHash = new byte[] { 1 },
            PartialVer = HashService.CurrentPartialVersion
        }});
        var svc = new PortableIndexService(volumes, files);
        Assert.True(svc.TryExport(vid, mount));
        var path = Path.Combine(mount, PortableIndexService.FileName);
        var good = ReadIndex(path);

        // Устаревший partial-хэш (например, файл писала старая версия приложения).
        good.Header!.PartialVer = HashService.CurrentPartialVersion - 1;
        WriteIndex(path, good);
        Assert.Null(svc.TryImport(vid, mount));

        // Чужая версия pHash.
        good.Header.PartialVer = HashService.CurrentPartialVersion;
        good.Header.PHashVer = ImageMatchService.CurrentPHashVersion + 1;
        WriteIndex(path, good);
        Assert.Null(svc.TryImport(vid, mount));

        // Незнакомая версия ФОРМАТА файла.
        good.Header.PHashVer = ImageMatchService.CurrentPHashVersion;
        good.Header.FormatVersion = PortableIndexService.FormatVersion + 1;
        WriteIndex(path, good);
        Assert.Null(svc.TryImport(vid, mount));

        // Контроль: с восстановленным заголовком тот же файл принимается.
        good.Header.FormatVersion = PortableIndexService.FormatVersion;
        WriteIndex(path, good);
        Assert.NotNull(svc.TryImport(vid, mount));
    }

    // ---------------- (д) Битый файл (обрезанный gzip) → отклонён без исключения ----------------

    [Fact]
    public void Import_rejects_corrupted_file_without_throwing()
    {
        var mount = NewMount("usb_bad");
        var db = new Database(Path.Combine(_root, "bad.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "USB-BAD", Label = "t" });
        files.UpsertMetaBatch(new[] { new FileRecord
        {
            VolumeId = vid, RelPath = "a.bin", Name = "a.bin", Size = 10,
            Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now,
            Category = FileCategory.Other, FullHash = new byte[] { 2 }
        }});
        var svc = new PortableIndexService(volumes, files);
        Assert.True(svc.TryExport(vid, mount));
        var path = Path.Combine(mount, PortableIndexService.FileName);

        // Обрезанный gzip (выдернули флешку во время записи чужим приложением и т.п.).
        var bytes = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());
        Assert.Null(svc.TryImport(vid, mount));

        // Вообще не gzip (мусор с тем же именем).
        File.WriteAllBytes(path, RandomBytes(512, 7));
        Assert.Null(svc.TryImport(vid, mount));

        // Пустой файл.
        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.Null(svc.TryImport(vid, mount));
    }

    [Fact]
    public void Import_rejects_decompression_bomb_before_materializing_json()
    {
        var mount = NewMount("usb_bomb");
        var db = new Database(Path.Combine(_root, "bomb.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "USB-BOMB", Label = "t" });
        var svc = new PortableIndexService(volumes, files);
        var path = Path.Combine(mount, PortableIndexService.FileName);

        // Валидный для JSON whitespace, который раздувается далеко за лимит. Физический gzip
        // остаётся маленьким, поэтому тест действительно проверяет распакованный, а не файловый кап.
        var block = Enumerable.Repeat((byte)' ', 1024 * 1024).ToArray();
        using (var fs = File.Create(path))
        using (var gzip = new GZipStream(fs, CompressionLevel.SmallestSize))
        {
            int blocks = checked((int)(PortableIndexService.MaxUncompressedBytes / block.Length) + 1);
            for (int i = 0; i < blocks; i++) gzip.Write(block);
        }

        Assert.Null(svc.TryImport(vid, mount));
    }

    // ---------------- (е) Изменённый файл (другой mtime) не переиспользуется из кэша ----------------

    [Fact]
    public async Task Changed_file_is_not_reused_from_portable_cache()
    {
        var mount = NewMount("usb_ch");
        var data = RandomBytes(60_000, 5);
        var dup2Path = Path.Combine(mount, "dup2.bin");
        File.WriteAllBytes(Path.Combine(mount, "dup1.bin"), data);
        File.WriteAllBytes(dup2Path, (byte[])data.Clone());

        var a = BuildMachine("ch_a", mount, "USB-CH");
        await ScanAsync(a);

        // После экспорта dup2 «изменился» (новый mtime, содержимое то же) — строгая тройная
        // сверка записи кэша обязана НЕ пройти, и файл пересчитывается чтением.
        File.SetLastWriteTimeUtc(dup2Path, DateTime.UtcNow.AddSeconds(9));

        var b = BuildMachine("ch_b", mount, "USB-CH");
        await ScanAsync(b);

        // dup1 переиспользован из кэша (не читался), dup2 — перехэширован (и частично, и полностью).
        Assert.DoesNotContain(b.Hash.PartialPaths, p => p.EndsWith("dup1.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(b.Hash.PartialPaths, p => p.EndsWith("dup2.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(b.Hash.FullPaths, p => p.EndsWith("dup1.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(b.Hash.FullPaths, p => p.EndsWith("dup2.bin", StringComparison.OrdinalIgnoreCase));

        // Содержимое реально совпадает — группа всё равно собрана, и с полной уверенностью.
        var groups = b.Groups.GetGroups(MatchType.Exact);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Members.Count);
        Assert.Equal(1.0, groups[0].Confidence);

        // dup1 при этом дал переиспользование — уведомление показано.
        Assert.Contains("Найден кэш проверки на носителе", b.Scan.Progress.Message ?? "");

        a.Scan.Dispose();
        b.Scan.Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
