using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Внезапное отключение носителя: операции над файлами при внезапно исчезнувшем источнике
/// (файл удалили или носитель извлекли между построением плана и выполнением) обязаны
/// (1) не терять и не портить остальные файлы, (2) не ронять пакет исключением,
/// (3) честно отчитываться понятными словами («недоступен», «Носитель отключён»).
/// </summary>
public class Sprint24YankTests : IDisposable
{
    private readonly string _dir;

    public Sprint24YankTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s24_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------------- (а) BatchRename: источник исчез между BuildPlan и Execute ----------------

    private RenameSourceFile MakeRenameSource(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        var fi = new FileInfo(path);
        return new RenameSourceFile(path.GetHashCode(), path, fi.CreationTimeUtc, fi.LastWriteTimeUtc, fi.Length, true);
    }

    [Fact]
    public void BatchRename_source_vanished_after_plan_rolls_back_without_orphans()
    {
        var svc = new BatchRenameService();
        var a = new byte[] { 1, 1, 1 };
        var c = new byte[] { 3, 3, 3 };
        var fa = MakeRenameSource("a.bin", a);
        var fb = MakeRenameSource("b.bin", new byte[] { 2 });
        var fc = MakeRenameSource("c.bin", c);

        var plan = svc.BuildPlan(new BatchRenameRequest(new[] { fa, fb, fc }, "Ночь", 1, 3, RenameSortOrder.Name));
        Assert.True(plan.CanExecute);

        // «Выдернули диск»: средний файл исчезает между предпросмотром и выполнением.
        File.Delete(fb.FullPath);

        var res = svc.Execute(plan, default);

        Assert.True(res.RolledBack);
        Assert.Equal(0, res.Renamed);
        Assert.Empty(res.Orphans); // все уцелевшие файлы вернулись на исходные имена
        // Понятный текст вместо системного сообщения.
        Assert.Contains("диск отключён", res.Error);

        // Уцелевшие файлы — на исходных местах и с исходным содержимым.
        Assert.Equal(a, File.ReadAllBytes(fa.FullPath));
        Assert.Equal(c, File.ReadAllBytes(fc.FullPath));
        // Никаких временных GUID-имён и «полупереименованных» файлов не осталось.
        var leftover = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a.bin", "c.bin" }, leftover);
    }

    // ---------------- (б) Распределение: источник исчез — остальные копируются, отчёт честный ----------------

    [Fact]
    public void Distribution_missing_source_counts_unavailable_and_copies_the_rest()
    {
        var alive = Path.Combine(_dir, "alive.jpg");
        var ghost = Path.Combine(_dir, "ghost.jpg");
        var content = new byte[10_000];
        new Random(24).NextBytes(content);
        File.WriteAllBytes(alive, content);
        File.WriteAllBytes(ghost, new byte[] { 9, 9 });

        var svc = new ResultDistributionService();
        var outRoot = Path.Combine(_dir, "out");
        var req = new DistributionRequest(new[]
        {
            new DistributionFile(1, alive, "Image", 0.96, content.Length),
            new DistributionFile(2, ghost, "Image", 0.96, 2)
        }, outRoot, DistributionMode.CopyFiles, VerifyAfterCopy: true);

        var plan = svc.BuildPlan(req);
        Assert.True(plan.CanExecute);

        // «Выдернули диск»: второй источник исчезает после построения плана.
        File.Delete(ghost);

        var res = svc.Execute(plan, req, default);

        Assert.Equal(1, res.Copied);
        Assert.Equal(1, res.Unavailable); // исчезнувший источник — «недоступен», а не «сбой»
        Assert.Equal(0, res.Failed);
        Assert.Null(res.Error);
        // Отчёт записан несмотря на пропавший источник (честный итог).
        Assert.NotNull(res.ReportPath);
        Assert.True(File.Exists(res.ReportPath!));

        var copied = Directory.GetFiles(outRoot, "*.jpg", SearchOption.AllDirectories);
        Assert.Single(copied);
        Assert.Equal(content, File.ReadAllBytes(copied[0]));
        // Временные файлы копирования не болтаются.
        Assert.Empty(Directory.GetFiles(outRoot, "*.tmp", SearchOption.AllDirectories));
        // Оригинал уцелевшего не тронут.
        Assert.True(File.Exists(alive));
    }

    // ---------------- (в) FileOpsService: несуществующий путь → false, без исключений ----------------

    private FileOpsService MakeOps()
    {
        var db = new Database(Path.Combine(_dir, "yank.db"));
        db.Initialize();
        return new FileOpsService(new OperationLogRepository(db), new FileRepository(db),
            new GroupRepository(db), new HashService(), Path.Combine(_dir, "q"));
    }

    [Fact]
    public void Delete_and_RecyclePath_on_missing_path_return_false_without_throwing()
    {
        var ops = MakeOps();
        var missing = Path.Combine(_dir, "no_such_dir", "ghost.bin");
        var rec = new FileRecord { Id = 1, VolumeId = 1, Name = "ghost.bin", RelPath = "ghost.bin", Size = 10 };

        // Файл уже исчез (диск выдернули) — операция «не удалась», но пакет не падает.
        Assert.False(ops.Delete(rec, missing, useRecycleBin: true, groupId: null));
        Assert.False(ops.Delete(rec, missing, useRecycleBin: false, groupId: null));
        Assert.False(ops.RecyclePath(missing));
    }

    // ---------------- (г) Консолидация: отключённый/выдернутый том — понятные ошибки, остальное скопировано ----------------

    private sealed class FakeVolumeService : IVolumeService
    {
        private readonly List<VolumeRecord> _catalog;
        public FakeVolumeService(params VolumeRecord[] vols) => _catalog = vols.ToList();
        public List<VolumeRecord> GetConnectedVolumes() => _catalog.Where(v => v.IsConnected).ToList();
        public List<VolumeRecord> SyncAndGetCatalog() => _catalog;
        public long Register(VolumeRecord volume) => volume.Id;
    }

    private static FileRecord Rec(long vid, string name, byte hashSeed, long size = 100) => new()
    {
        VolumeId = vid, RelPath = name, Name = name, Ext = ".bin", Size = size,
        Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now, Category = FileCategory.Other,
        FullHash = new byte[] { hashSeed, 42, 42, 42 }, ScanState = ScanState.Full
    };

    [Fact]
    public void Consolidation_offline_and_yanked_volumes_fail_items_clearly_and_copy_the_rest()
    {
        var db = new Database(Path.Combine(_dir, "cons.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);

        // Том 1 — подключён, файл реально существует.
        var srcDir = Path.Combine(_dir, "cons_src");
        Directory.CreateDirectory(srcDir);
        var online = new VolumeRecord { Serial = "S24on", Label = "Живой", MountPath = srcDir, IsConnected = true };
        online.Id = volumes.Upsert(online);
        File.WriteAllBytes(Path.Combine(srcDir, "alive.bin"), new byte[] { 1, 2, 3 });

        // Том 2 — отключён по каталогу (обычный офлайн-слепок).
        var offline = new VolumeRecord { Serial = "S24off", Label = "Офлайн", MountPath = null, IsConnected = false };
        offline.Id = volumes.Upsert(offline);

        // Том 3 — «выдернут»: каталог ещё считает его подключённым, но корня уже нет.
        var yankedMount = Path.Combine(_dir, "cons_yanked");
        var yanked = new VolumeRecord { Serial = "S24yank", Label = "Выдернут", MountPath = yankedMount, IsConnected = true };
        yanked.Id = volumes.Upsert(yanked);

        files.UpsertMetaBatch(new List<FileRecord>
        {
            Rec(online.Id, "alive.bin", 1, size: 3),
            Rec(offline.Id, "off.bin", 2),
            Rec(yanked.Id, "gone.bin", 3)
        });

        var cons = new ConsolidationService(files, new FakeVolumeService(online, offline, yanked));
        var target = Path.Combine(_dir, "cons_target");
        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { online.Id, offline.Id, yanked.Id },
            TargetPath = target,
            PreserveFolderStructure = false
        };

        var plan = cons.BuildPlan(options);
        Assert.Equal(3, plan.UniqueCount); // содержимое разное — копировать планируется всё

        var report = cons.Execute(plan, options, null, default);

        // Доступный файл скопирован; недоступные — честные пофайловые ошибки, пакет не упал.
        Assert.Equal(1, report.Copied);
        Assert.Equal(2, report.Failed);
        Assert.Contains(report.Errors, e => e.Contains("Носитель отключён") && e.Contains("off.bin"));
        Assert.Contains(report.Errors, e => e.Contains("Носитель отключён") && e.Contains("gone.bin"));
        Assert.True(File.Exists(Path.Combine(target, "alive.bin")));
        // Источник не тронут (консолидация только копирует).
        Assert.True(File.Exists(Path.Combine(srcDir, "alive.bin")));
    }
}
