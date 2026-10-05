using System.Runtime.InteropServices;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Scope-aware группировка и hardlink-aware reclaimable.
/// </summary>
public class Sprint6ScopeHardlinkTests : IDisposable
{
    private readonly string _dir;

    public Sprint6ScopeHardlinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private (Database db, FileRepository files, GroupRepository groups, LshRepository lsh, VolumeRecord vol, HashService hash) Setup(string dbName)
    {
        var db = new Database(Path.Combine(_dir, dbName));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();
        var vol = new VolumeRecord { Serial = "S6", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);
        return (db, files, groups, lsh, vol, hash);
    }

    private static FileRecord Rec(long volId, string rel, long size, byte[] full) => new()
    {
        VolumeId = volId, RelPath = rel, Name = Path.GetFileName(rel), Ext = Path.GetExtension(rel),
        Size = size, Mtime = DateTimeOffset.Now, Category = FileCategory.Other, FullHash = full, ScanState = ScanState.Full
    };

    [Fact]
    public void Exact_grouping_is_scope_aware()
    {
        // X в папке A и Y в папке B — байт-идентичны, один full_hash. В обычном режиме результат
        // должен соответствовать выбранной области: если выбрана только B, группы из A показывать нельзя.
        Directory.CreateDirectory(Path.Combine(_dir, "A"));
        Directory.CreateDirectory(Path.Combine(_dir, "B"));
        var data = new byte[100_000];
        new Random(6).NextBytes(data);
        File.WriteAllBytes(Path.Combine(_dir, "A", "x.bin"), data);
        File.WriteAllBytes(Path.Combine(_dir, "B", "y.bin"), (byte[])data.Clone());

        var (_, files, groups, lsh, vol, hash) = Setup("scope.db");
        var full = hash.ComputeFullHash(Path.Combine(_dir, "A", "x.bin"));
        files.UpsertMetaBatch(new List<FileRecord>
        {
            Rec(vol.Id, Path.Combine("A", "x.bin"), data.Length, full),
            Rec(vol.Id, Path.Combine("B", "y.bin"), data.Length, full),
        });

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };

        // Область = только папка B → один in-scope файл → группы нет.
        bool InB(FileRecord f) => f.RelPath.StartsWith("B" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, grouper.BuildExactGroups(dict, InB, RepresentativeRule.Oldest, null, default));
        Assert.Empty(groups.GetGroups(MatchType.Exact));

        // Область = всё → оба файла in-scope → одна группа.
        Assert.Equal(1, grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default));
        Assert.Single(groups.GetGroups(MatchType.Exact));
    }

    [Fact]
    public void Hardlink_only_cluster_yields_no_reclaimable_group()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // hardlink — Windows/NTFS

        var data = new byte[8192];
        new Random(9).NextBytes(data);
        var rep = Path.Combine(_dir, "rep.bin");
        var dup = Path.Combine(_dir, "dup.bin");
        File.WriteAllBytes(rep, data);
        File.WriteAllBytes(dup, (byte[])data.Clone());

        var (db, files, groups, lsh, vol, hash) = Setup("hl.db");

        // Делаем dup жёсткой ссылкой на rep — теперь это ОДИН физический файл под двумя путями.
        var ops = new FileOpsService(new OperationLogRepository(db), files, groups, hash, Path.Combine(_dir, "q"));
        bool linked = ops.ReplaceWithHardLink(new FileRecord { Name = "dup.bin", Size = data.Length }, dup, rep, null);
        if (!linked) return; // не NTFS/нет прав — тест неприменим

        var full = hash.ComputeFullHash(rep);
        files.UpsertMetaBatch(new List<FileRecord>
        {
            Rec(vol.Id, "rep.bin", data.Length, full),
            Rec(vol.Id, "dup.bin", data.Length, full),
        });

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };

        // Оба пути — один физический файл → схлопываются → удалять нечего → группы нет.
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default);
        Assert.Equal(0, created);
        Assert.Empty(groups.GetGroups(MatchType.Exact));
    }

    [Fact]
    public void Reclaimable_excludes_files_pinned_by_other_hardlinks()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var data = new byte[8192];
        new Random(11).NextBytes(data);
        var a = Path.Combine(_dir, "a.bin");
        var b = Path.Combine(_dir, "b.bin");
        var c = Path.Combine(_dir, "c.bin");
        File.WriteAllBytes(a, data);
        File.WriteAllBytes(b, (byte[])data.Clone());
        File.WriteAllBytes(c, (byte[])data.Clone());

        var (db, files, groups, lsh, vol, hash) = Setup("rec.db");
        var ops = new FileOpsService(new OperationLogRepository(db), files, groups, hash, Path.Combine(_dir, "q"));
        // b → hardlink на a, поэтому у inode файла a становится 2 ссылки.
        if (!ops.ReplaceWithHardLink(new FileRecord { Name = "b.bin", Size = data.Length }, b, a, null)) return;

        var full = hash.ComputeFullHash(a);
        var recA = Rec(vol.Id, "a.bin", data.Length, full);             // mtime = сейчас
        var recC = Rec(vol.Id, "c.bin", data.Length, full);
        recC.Mtime = DateTimeOffset.Now.AddDays(-1);                     // c старее → станет эталоном (Oldest)
        files.UpsertMetaBatch(new List<FileRecord> { recA, recC });

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        int created = grouper.BuildExactGroups(new Dictionary<long, VolumeRecord> { [vol.Id] = vol },
            _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(1, created);
        var g = Assert.Single(groups.GetGroups(MatchType.Exact));
        // Эталон = c. Не-эталон a пришпилен hardlink-ом b → удаление a не освободит место → reclaimable 0.
        Assert.Equal(0, g.ReclaimableBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
