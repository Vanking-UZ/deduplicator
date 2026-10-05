using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Сквозные тесты всего конвейера на реальных файлах: реальный ScanService прогоняет
/// корпус (точные дубли, похожие фото, мусор), затем проверяются группы, статистика,
/// удаление/восстановление, консолидация и очистка.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _root;

    public EndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dedup_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private sealed record Ctx(
        FileRepository Files, GroupRepository Groups, VolumeRepository Volumes,
        OperationLogRepository Log, IVolumeService VolSvc, IScanService Scan,
        IFileOpsService FileOps, IConsolidationService Cons, IStatisticsService Stats,
        long VolumeId, string MountPath, string Corpus);

    private async Task<Ctx> BuildAndScanAsync(ScanDepth depth)
    {
        var corpus = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(corpus);

        // Точные дубли (одинаковые байты) + уникальный файл.
        var data = new byte[60_000];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(Path.Combine(corpus, "exact1.bin"), data);
        File.WriteAllBytes(Path.Combine(corpus, "exact2.bin"), (byte[])data.Clone());
        File.WriteAllBytes(Path.Combine(corpus, "unique.bin"),
            Enumerable.Range(0, 4321).Select(i => (byte)(i * 7)).ToArray());

        // Похожие фото: то же изображение в двух разрешениях (реальный near-duplicate).
        using (var img = new Image<Rgba32>(256, 256))
        {
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        row[x] = new Rgba32((byte)x, (byte)y, (byte)((x + y) / 2), 255);
                }
            });
            img.SaveAsPng(Path.Combine(corpus, "pic.png"));
            using var small = img.Clone(c => c.Resize(160, 160));
            small.SaveAsPng(Path.Combine(corpus, "pic_small.png"));
        }

        // Мусор: пустая папка, файл 0 байт, временный файл.
        Directory.CreateDirectory(Path.Combine(corpus, "emptyFolder"));
        File.WriteAllBytes(Path.Combine(corpus, "zero.dat"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(corpus, "scratch.tmp"), "tmp");

        // Сервисы (реальные, как в приложении).
        var db = new Database(Path.Combine(_root, "e2e.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var log = new OperationLogRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, "app")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_root, "thumbs"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings, new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));
        var fileOps = new FileOpsService(log, files, groups, hash, Path.Combine(_root, "quarantine"));
        var cons = new ConsolidationService(files, volSvc);
        var stats = new StatisticsService(groups, files);

        var options = new ScanOptions
        {
            Folders = { corpus },
            Depth = depth,
            Parallelism = 2,
            Incremental = true,
            ImageThreshold = 0.80,
            AudioThreshold = 0.80,
            SkipHiddenSystem = false
        };
        await scan.RunAsync(options, default);

        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);

        var catalog = volSvc.SyncAndGetCatalog();
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(corpus));
        var vol = catalog.First(v => v.IsConnected && v.MountPath is not null &&
            string.Equals(Path.GetPathRoot(v.MountPath), driveRoot, StringComparison.OrdinalIgnoreCase));

        return new Ctx(files, groups, volumes, log, volSvc, scan, fileOps, cons, stats, vol.Id, vol.MountPath!, corpus);
    }

    [Fact]
    public async Task Full_scan_finds_exact_duplicates_and_statistics()
    {
        var ctx = await BuildAndScanAsync(ScanDepth.ExactOnly);

        var exact = ctx.Groups.GetGroups(MatchType.Exact);
        Assert.NotEmpty(exact);
        var grp = exact.First(g => g.Members.Count >= 2);
        var names = grp.Members.Select(m => m.File!.Name).OrderBy(n => n).ToList();
        Assert.Contains("exact1.bin", names);
        Assert.Contains("exact2.bin", names);

        var s = ctx.Stats.Compute();
        Assert.True(s.ExactGroups >= 1);
        Assert.True(s.TotalReclaimableBytes >= 60_000);
        Assert.True(s.TotalIndexedFiles >= 5);
    }

    [Fact]
    public async Task Full_scan_finds_similar_images_across_formats()
    {
        var ctx = await BuildAndScanAsync(ScanDepth.WithSimilarImages);

        var similar = ctx.Groups.GetGroups(MatchType.SimilarImage);
        Assert.NotEmpty(similar);
        var members = similar.SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("pic.png", members);
        Assert.Contains("pic_small.png", members);
    }

    [Fact]
    public async Task Delete_to_quarantine_then_restore_roundtrip()
    {
        var ctx = await BuildAndScanAsync(ScanDepth.ExactOnly);
        var exact = ctx.Groups.GetGroups(MatchType.Exact).First(g => g.Members.Count >= 2);
        var victim = exact.Members.First(m => m.File!.Id != exact.RepresentativeId).File!;
        // RelPath относителен корня тома, поэтому полный путь = точка монтирования + RelPath.
        var fullPath = Path.Combine(ctx.MountPath, victim.RelPath);

        Assert.True(File.Exists(fullPath));
        bool deleted = ctx.FileOps.Delete(victim, fullPath, useRecycleBin: false, groupId: exact.Id);
        Assert.True(deleted);
        Assert.False(File.Exists(fullPath)); // перемещён в карантин

        var entry = ctx.Log.GetAll(onlyReversible: true).First(e => e.FileName == victim.Name);
        Assert.True(ctx.FileOps.Restore(entry));
        Assert.True(File.Exists(fullPath)); // восстановлен на место
    }

    [Fact]
    public async Task Consolidation_copies_unique_and_skips_duplicates()
    {
        var ctx = await BuildAndScanAsync(ScanDepth.ExactOnly);
        var target = Path.Combine(_root, "archive");

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { ctx.VolumeId },
            TargetPath = target,
            Rule = RepresentativeRule.Oldest,
            PreserveFolderStructure = false
        };

        var plan = ctx.Cons.BuildPlan(options);
        Assert.True(plan.SkippedCount >= 1);   // один из точных дублей пропущен
        Assert.True(plan.UniqueCount >= 3);    // unique + pic.png + pic.jpg + один из точных

        var report = ctx.Cons.Execute(plan, options, null, default);
        Assert.True(report.Copied >= 3);
        Assert.Equal(0, report.Failed);

        var copied = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length;
        Assert.Equal(report.Copied, copied);
    }

    [Fact]
    public async Task Junk_scan_finds_empty_folder_zero_and_temp()
    {
        var ctx = await BuildAndScanAsync(ScanDepth.ExactOnly);
        var junk = new JunkScanService().Scan(new JunkScanOptions
        {
            Roots = { ctx.Corpus },
            EmptyFolders = true, ZeroByteFiles = true, TempFiles = true,
            BrokenShortcuts = false, LargeFiles = false
        }, null, default);

        Assert.Contains(junk, j => j.Kind == JunkKind.EmptyFolder && j.Path.EndsWith("emptyFolder"));
        Assert.Contains(junk, j => j.Kind == JunkKind.ZeroByteFile && j.Path.EndsWith("zero.dat"));
        Assert.Contains(junk, j => j.Kind == JunkKind.TempFile && j.Path.EndsWith("scratch.tmp"));
    }

    [Fact]
    public async Task Scan_two_folders_same_volume_keeps_both_and_finds_cross_folder_dup()
    {
        // Регрессия: при сканировании двух папок одного диска (режим Folders) прунинг по всему
        // тому удалял записи первой папки → межпапочные дубли терялись, в индексе оставалась
        // только последняя папка. Теперь индексируются обе.
        var f1 = Path.Combine(_root, "folderA");
        var f2 = Path.Combine(_root, "folderB");
        Directory.CreateDirectory(f1);
        Directory.CreateDirectory(f2);

        var shared = new byte[50_000];
        new Random(7).NextBytes(shared);
        File.WriteAllBytes(Path.Combine(f1, "shared.bin"), shared);
        File.WriteAllBytes(Path.Combine(f2, "shared_copy.bin"), (byte[])shared.Clone());
        File.WriteAllBytes(Path.Combine(f1, "onlyA.bin"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(f2, "onlyB.bin"), new byte[] { 4, 5, 6, 7 });

        var db = new Database(Path.Combine(_root, "multi.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, "app2")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_root, "thumbs2"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings, new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));

        await scan.RunAsync(new ScanOptions
        {
            Folders = { f1, f2 },
            Depth = ScanDepth.ExactOnly,
            Parallelism = 2,
            SkipHiddenSystem = false
        }, default);
        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);

        var (total, _) = files.TotalIndexed();
        Assert.Equal(4, total); // обе папки остаются в индексе (раньше было 2)

        var names = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("shared.bin", names);
        Assert.Contains("shared_copy.bin", names); // межпапочный дубль найден

        scan.Dispose();
    }

    [Fact]
    public async Task Folder_scan_does_not_hash_files_outside_selected_folder()
    {
        var selected = Path.Combine(_root, "selectedOnly");
        var outside = Path.Combine(_root, "outsideSelected");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(outside);

        var data = new byte[50_000];
        new Random(11).NextBytes(data);
        File.WriteAllBytes(Path.Combine(selected, "a.bin"), data);
        File.WriteAllBytes(Path.Combine(selected, "b.bin"), (byte[])data.Clone());
        var outsidePath = Path.Combine(outside, "outside.bin");
        File.WriteAllBytes(outsidePath, (byte[])data.Clone());

        var db = new Database(Path.Combine(_root, "folder_scope.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, "app_scope")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_root, "thumbs_scope"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings, new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));

        var catalog = volSvc.SyncAndGetCatalog();
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(selected));
        var vol = catalog.First(v => v.IsConnected && v.MountPath is not null &&
            string.Equals(Path.GetPathRoot(v.MountPath), driveRoot, StringComparison.OrdinalIgnoreCase));
        var outsideInfo = new FileInfo(outsidePath);
        var outsideRecord = new FileRecord
        {
            VolumeId = vol.Id,
            RelPath = Path.GetRelativePath(vol.MountPath!, outsidePath),
            Name = Path.GetFileName(outsidePath),
            Ext = ".bin",
            Size = outsideInfo.Length,
            Mtime = outsideInfo.LastWriteTime,
            Created = outsideInfo.CreationTime,
            Category = FileCategory.Other,
            ScanState = ScanState.Meta,
            LastIndexed = DateTimeOffset.Now
        };
        files.UpsertMetaBatch(new[] { outsideRecord });

        await scan.RunAsync(new ScanOptions
        {
            Folders = { selected },
            Depth = ScanDepth.ExactOnly,
            Parallelism = 2,
            SkipHiddenSystem = false
        }, default);

        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);
        var outsideAfter = files.GetById(outsideRecord.Id)!;
        Assert.Null(outsideAfter.PartialHash);
        Assert.Null(outsideAfter.FullHash);

        var names = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("a.bin", names);
        Assert.Contains("b.bin", names);
        Assert.DoesNotContain("outside.bin", names);

        scan.Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
