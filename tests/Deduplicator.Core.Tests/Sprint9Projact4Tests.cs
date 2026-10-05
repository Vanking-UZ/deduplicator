using System;
using System.Collections.Generic;
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

namespace Deduplicator.Core.Tests;

/// <summary>Фильтр по категориям файлов (P0-A) — сквозные тесты на реальном конвейере.</summary>
public class Sprint9Projact4Tests : IDisposable
{
    private readonly string _root;

    public Sprint9Projact4Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dedup_p4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private (IScanService scan, FileRepository files, GroupRepository groups, string corpus) Build(string name)
    {
        var corpus = Path.Combine(_root, name);
        Directory.CreateDirectory(corpus);

        // 2 одинаковых PNG (Image) + 2 одинаковых .bin (Other) — точные дубли в каждой категории.
        using (var img = new Image<Rgba32>(64, 64))
        {
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++) row[x] = new Rgba32((byte)x, (byte)y, 100, 255);
                }
            });
            img.SaveAsPng(Path.Combine(corpus, "p1.png"));
        }
        File.Copy(Path.Combine(corpus, "p1.png"), Path.Combine(corpus, "p2.png"));

        var blob = new byte[40_000];
        new Random(3).NextBytes(blob);
        File.WriteAllBytes(Path.Combine(corpus, "b1.bin"), blob);
        File.WriteAllBytes(Path.Combine(corpus, "b2.bin"), (byte[])blob.Clone());

        var db = new Database(Path.Combine(_root, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, name + "_app")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_root, name + "_thumbs"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings,
            new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));
        return (scan, files, groups, corpus);
    }

    [Fact]
    public async Task Scan_with_only_images_does_not_index_other_categories()
    {
        var (scan, files, groups, corpus) = Build("onlyimg");
        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus },
            Depth = ScanDepth.ExactOnly,
            SkipHiddenSystem = false,
            IncludedCategories = new HashSet<FileCategory> { FileCategory.Image }
        }, default);

        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);

        var (total, _) = files.TotalIndexed();
        Assert.Equal(2, total); // только 2 PNG; .bin (Other) не индексировались

        var names = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("p1.png", names);
        Assert.Contains("p2.png", names);
        Assert.DoesNotContain("b1.bin", names);
        Assert.DoesNotContain("b2.bin", names);
    }

    [Fact]
    public async Task Scan_with_only_other_excludes_images()
    {
        var (scan, files, groups, corpus) = Build("onlyother");
        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus },
            Depth = ScanDepth.ExactOnly,
            SkipHiddenSystem = false,
            IncludedCategories = new HashSet<FileCategory> { FileCategory.Other }
        }, default);

        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);
        var names = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("b1.bin", names);
        Assert.DoesNotContain("p1.png", names);
    }

    [Fact]
    public async Task Scan_with_no_categories_fails_validation()
    {
        var (scan, _, _, corpus) = Build("nocat");
        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus },
            Depth = ScanDepth.ExactOnly,
            SkipHiddenSystem = false,
            IncludedCategories = new HashSet<FileCategory>() // пусто → ошибка валидации
        }, default);

        Assert.Equal(ScanStage.Failed, scan.Progress.Stage);
    }

    [Fact]
    public async Task Category_limited_rescan_excludes_other_category_from_groups()
    {
        // Регрессия: после полного скана (все категории) .bin уже в индексе с хэшами.
        // Пере-скан «только фото» НЕ должен показывать старые .bin как удаляемые exact-группы.
        var (scan, _, groups, corpus) = Build("recat");

        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus }, Depth = ScanDepth.ExactOnly, SkipHiddenSystem = false
        }, default);
        var afterFull = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("b1.bin", afterFull); // при полном скане .bin-дубль есть

        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus }, Depth = ScanDepth.ExactOnly, SkipHiddenSystem = false,
            IncludedCategories = new HashSet<FileCategory> { FileCategory.Image }
        }, default);

        var afterImages = groups.GetGroups(MatchType.Exact)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.DoesNotContain("b1.bin", afterImages); // .bin (Other) больше не всплывает
        Assert.Contains("p1.png", afterImages);        // а фото-дубли остаются
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>Безопасное групповое переименование.</summary>
public class BatchRenameTests : IDisposable
{
    private readonly string _dir;
    private readonly BatchRenameService _svc = new();

    public BatchRenameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_ren_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private RenameSourceFile Make(string name, byte[] content, bool canModify = true)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        var fi = new FileInfo(path);
        return new RenameSourceFile(path.GetHashCode(), path, fi.CreationTimeUtc, fi.LastWriteTimeUtc, fi.Length, canModify);
    }

    [Fact]
    public void BuildPlan_adds_numbering_and_preserves_extension()
    {
        var files = new[] { Make("a.JPG", new byte[] { 1 }), Make("b.png", new byte[] { 2 }) };
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "Отпуск", 1, 3, RenameSortOrder.Name));

        Assert.True(plan.CanExecute);
        Assert.Equal("Отпуск_001.JPG", plan.Items[0].NewName);
        Assert.Equal("Отпуск_002.png", plan.Items[1].NewName);
    }

    [Fact]
    public void BuildPlan_detects_existing_file_conflict()
    {
        var files = new[] { Make("x.bin", new byte[] { 1 }) };
        File.WriteAllBytes(Path.Combine(_dir, "Имя_001.bin"), new byte[] { 9 }); // занятое имя вне набора
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "Имя", 1, 3, RenameSortOrder.AsIs));

        Assert.False(plan.CanExecute);
        Assert.NotEmpty(plan.Conflicts);
    }

    [Fact]
    public void BuildPlan_sanitizes_invalid_characters()
    {
        var files = new[] { Make("a.bin", new byte[] { 1 }) };
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "a/b:c*?", 1, 2, RenameSortOrder.AsIs));
        Assert.DoesNotContain('/', plan.Items[0].NewName);
        Assert.DoesNotContain(':', plan.Items[0].NewName);
        Assert.DoesNotContain('*', plan.Items[0].NewName);
    }

    [Fact]
    public void Execute_two_phase_handles_shift_without_collision()
    {
        // X_002 (A) и X_003 (B), переименовываем base X с 1 → X_001, X_002.
        // X_003→X_002 наезжает на ещё существующий X_002 — без двухфазности сломалось бы.
        var a = new byte[] { 0xAA }; var b = new byte[] { 0xBB };
        var files = new[] { Make("X_002.bin", a), Make("X_003.bin", b) };
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "X", 1, 3, RenameSortOrder.Name));
        Assert.True(plan.CanExecute);

        var res = _svc.Execute(plan, default);
        Assert.Equal(2, res.Renamed);
        Assert.False(res.RolledBack);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_dir, "X_001.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(_dir, "X_002.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "X_003.bin")));
    }

    [Fact]
    public void Execute_rolls_back_on_failure()
    {
        var real = Make("real.bin", new byte[] { 7, 7, 7 });
        // Второй «файл» не существует на диске → File.Move в фазе 1 бросит → должен откатиться real.
        var ghostPath = Path.Combine(_dir, "ghost.bin");
        var ghost = new RenameSourceFile(123, ghostPath, DateTimeOffset.Now, DateTimeOffset.Now, 1, true);
        var plan = _svc.BuildPlan(new BatchRenameRequest(new[] { real, ghost }, "N", 1, 3, RenameSortOrder.AsIs));

        var res = _svc.Execute(plan, default);
        Assert.True(res.RolledBack);
        Assert.Equal(0, res.Renamed);
        Assert.True(File.Exists(real.FullPath)); // исходный файл вернулся на место
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(real.FullPath));
    }

    [Fact]
    public void BuildPlan_skips_unavailable_or_protected()
    {
        var files = new[]
        {
            Make("ok.bin", new byte[] { 1 }),
            Make("locked.bin", new byte[] { 2 }, canModify: false)
        };
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "N", 1, 3, RenameSortOrder.Name));
        Assert.Contains(plan.Items, i => i.Skipped && i.OldName == "locked.bin");
        Assert.Contains(plan.Items, i => !i.Skipped && i.OldName == "ok.bin");
    }

    [Fact]
    public void Execute_idempotent_rerun_same_names_is_not_a_conflict()
    {
        // Файлы уже названы как результат — повторный план не должен ложно конфликтовать сам с собой.
        var files = new[] { Make("Vacation_001.bin", new byte[] { 1 }) };
        var plan = _svc.BuildPlan(new BatchRenameRequest(files, "Vacation", 1, 3, RenameSortOrder.AsIs));
        Assert.True(plan.CanExecute);
        Assert.Empty(plan.Conflicts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>Профили похожести (предфильтр/пресеты) и безопасное распределение.</summary>
public class ProfileAndDistributionTests : IDisposable
{
    private readonly string _dir;

    public ProfileAndDistributionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_pd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Profile_size_range_filters_candidates()
    {
        var p = new SimilarityProfile { MinSize = 1000, MaxSize = 5000 };
        var now = DateTimeOffset.Now;
        Assert.False(SimilarityProfileService.Matches(FileCategory.Image, 500, now, now, "a.jpg", p));
        Assert.True(SimilarityProfileService.Matches(FileCategory.Image, 2000, now, now, "a.jpg", p));
        Assert.False(SimilarityProfileService.Matches(FileCategory.Image, 9000, now, now, "a.jpg", p));
    }

    [Fact]
    public void Profile_created_range_and_extension_filter()
    {
        var p = new SimilarityProfile
        {
            CreatedFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IncludeExtensions = new() { "jpg" }
        };
        var inRange = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var tooOld = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(SimilarityProfileService.Matches(FileCategory.Image, 10, inRange, inRange, "a.jpg", p));
        Assert.False(SimilarityProfileService.Matches(FileCategory.Image, 10, tooOld, tooOld, "a.jpg", p));
        Assert.False(SimilarityProfileService.Matches(FileCategory.Image, 10, inRange, inRange, "a.png", p));
    }

    [Fact]
    public void Profile_presets_widen_threshold()
    {
        Assert.True(SimilarityProfileService.Strict().ImageThreshold > SimilarityProfileService.Wide().ImageThreshold);
    }

    [Fact]
    public void Profile_json_roundtrip()
    {
        var svc = new SimilarityProfileService(Path.Combine(_dir, "profiles.json"));
        var profiles = new[] { SimilarityProfileService.Normal(), new SimilarityProfile { Name = "Мой", MinSize = 100 } };
        svc.Save(profiles);
        var loaded = svc.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, p => p.Name == "Мой" && p.MinSize == 100);
    }

    [Fact]
    public void Distribution_report_only_writes_csv_and_does_not_touch_originals()
    {
        var src = Path.Combine(_dir, "src.jpg");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var svc = new ResultDistributionService();
        var outRoot = Path.Combine(_dir, "out");
        var req = new DistributionRequest(
            new[] { new DistributionFile(1, src, "Image", 0.96, 3) }, outRoot, DistributionMode.ReportOnly);

        var plan = svc.BuildPlan(req);
        Assert.True(plan.CanExecute);
        Assert.Contains("Очень похожие", plan.Items[0].FolderKey);

        var res = svc.Execute(plan, req, default);
        Assert.NotNull(res.ReportPath);
        Assert.True(File.Exists(res.ReportPath!));
        Assert.Equal(0, res.Copied);
        Assert.True(File.Exists(src));
        Assert.Empty(Directory.GetFiles(outRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void Distribution_copy_verifies_and_keeps_source()
    {
        var src = Path.Combine(_dir, "photo.png");
        var content = new byte[20_000];
        new Random(5).NextBytes(content);
        File.WriteAllBytes(src, content);

        var svc = new ResultDistributionService();
        var outRoot = Path.Combine(_dir, "copyout");
        var req = new DistributionRequest(
            new[] { new DistributionFile(1, src, "Image", 0.88, content.Length) },
            outRoot, DistributionMode.CopyFiles, VerifyAfterCopy: true);

        var plan = svc.BuildPlan(req);
        var res = svc.Execute(plan, req, default);

        Assert.Equal(1, res.Copied);
        Assert.Equal(0, res.Failed);
        Assert.True(File.Exists(src));
        var copies = Directory.GetFiles(outRoot, "photo.png", SearchOption.AllDirectories);
        Assert.Single(copies);
        Assert.Equal(content, File.ReadAllBytes(copies[0]));
        Assert.Contains("Похожие", copies[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
