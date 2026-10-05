using System.Collections.Concurrent;
using Deduplicator.Core.Data;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>Session-scoped группы и пропуск скрытых папок.</summary>
public class Sprint7ProjAct3Tests : IDisposable
{
    private readonly string _dir;

    public Sprint7ProjAct3Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Groups_are_scoped_to_session_old_ones_cleared()
    {
        var db = new Database(Path.Combine(_dir, "sess.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var vol = new VolumeRecord { Serial = "S7", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        var recs = Enumerable.Range(0, 4).Select(i => new FileRecord
        {
            VolumeId = vol.Id, RelPath = $"f{i}.bin", Name = $"f{i}.bin", Size = 10 + i,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image, ScanState = ScanState.Full
        }).ToList();
        files.UpsertMetaBatch(recs);

        DuplicateGroup G(MatchType t, FileRecord a, FileRecord b) => new()
        {
            MatchType = t, Confidence = 1.0, RepresentativeId = a.Id, ReclaimableBytes = 1,
            Members = { new DuplicateMember { FileId = a.Id }, new DuplicateMember { FileId = b.Id } }
        };

        // Сессия 1: похожие фото. Сессия 2: точные.
        groups.InsertGroups(new[] { G(MatchType.SimilarImage, recs[0], recs[1]) }, sessionId: 1);
        groups.InsertGroups(new[] { G(MatchType.Exact, recs[2], recs[3]) }, sessionId: 2);

        // Новый скан (сессия 2) чистит группы прошлых сессий.
        groups.ClearGroupsNotInSession(2);

        Assert.Single(groups.GetGroups(MatchType.Exact));       // группа сессии 2 осталась
        Assert.Empty(groups.GetGroups(MatchType.SimilarImage)); // старая «похожая» из сессии 1 убрана
    }

    [Fact]
    public void ClearGroupsInSession_removes_only_current_session_partial_groups()
    {
        var db = new Database(Path.Combine(_dir, "partial.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var vol = new VolumeRecord { Serial = "S7P", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);
        var recs = Enumerable.Range(0, 4).Select(i => new FileRecord
        {
            VolumeId = vol.Id, RelPath = $"p{i}.bin", Name = $"p{i}.bin", Size = 10 + i,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image, ScanState = ScanState.Full
        }).ToList();
        files.UpsertMetaBatch(recs);

        DuplicateGroup G(MatchType t, FileRecord a, FileRecord b) => new()
        {
            MatchType = t, Confidence = 1.0, RepresentativeId = a.Id, ReclaimableBytes = 1,
            Members = { new DuplicateMember { FileId = a.Id }, new DuplicateMember { FileId = b.Id } }
        };

        groups.InsertGroups(new[] { G(MatchType.SimilarImage, recs[0], recs[1]) }, sessionId: 1); // прошлая
        groups.InsertGroups(new[] { G(MatchType.Exact, recs[2], recs[3]) }, sessionId: 2);        // текущая (частичная)

        // Отмена скана сессии 2 → убираем только её частичные группы; прошлая (сессия 1) не трогается тут.
        groups.ClearGroupsInSession(2);

        Assert.Empty(groups.GetGroups(MatchType.Exact));        // частичные группы прерванного скана убраны
        Assert.Single(groups.GetGroups(MatchType.SimilarImage)); // прошлая сессия не задета этим вызовом
    }

    [Fact]
    public void RemoveMember_promotes_new_representative_when_old_one_removed()
    {
        var db = new Database(Path.Combine(_dir, "rep.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var vol = new VolumeRecord { Serial = "S7R", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);
        var recs = Enumerable.Range(0, 3).Select(i => new FileRecord
        {
            VolumeId = vol.Id, RelPath = $"r{i}.bin", Name = $"r{i}.bin", Size = 10 + i,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Other, ScanState = ScanState.Full
        }).ToList();
        files.UpsertMetaBatch(recs);

        var g = new DuplicateGroup
        {
            MatchType = MatchType.Exact, Confidence = 1.0, RepresentativeId = recs[0].Id, ReclaimableBytes = 20,
            Members =
            {
                new DuplicateMember { FileId = recs[0].Id },
                new DuplicateMember { FileId = recs[1].Id },
                new DuplicateMember { FileId = recs[2].Id }
            }
        };
        groups.InsertGroups(new[] { g }, sessionId: 1);
        long gid = groups.GetGroups(MatchType.Exact).Single().Id;

        // Удаляем ЭТАЛОН (recs[0]). Группа из 3 → остаётся 2 → должен назначиться новый эталон.
        groups.RemoveMember(gid, recs[0].Id);

        var after = groups.GetGroups(MatchType.Exact).Single();
        Assert.NotNull(after.RepresentativeId);
        Assert.NotEqual(recs[0].Id, after.RepresentativeId);                 // не удалённый
        Assert.Contains(after.RepresentativeId!.Value, new[] { recs[1].Id, recs[2].Id }); // один из оставшихся
        Assert.Contains(after.Members, m => m.IsRepresentative);            // эталон существует среди членов
    }

    [Fact]
    public void Fallback_enumerator_skips_hidden_system_directories()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_dir, "visible"));
        var hidden = Path.Combine(_dir, "hidden");
        Directory.CreateDirectory(hidden);
        File.WriteAllBytes(Path.Combine(_dir, "visible", "a.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(hidden, "b.bin"), new byte[] { 2 }); // сам файл НЕ скрытый
        new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;

        var errors = new ConcurrentBag<FastDedup.Engine.ScanError>();

        // SkipHiddenSystem=true → внутрь скрытой папки не заходим, b.bin не попадает.
        var skip = new FastDedup.Engine.FallbackEnumerator()
            .EnumerateFiles(_dir, new FastDedup.Engine.ScanOptions { Roots = new[] { _dir }, SkipHiddenSystem = true }, errors, default)
            .ToList();
        Assert.Contains(skip, p => p.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(skip, p => p.EndsWith("b.bin", StringComparison.OrdinalIgnoreCase));

        // SkipHiddenSystem=false → b.bin виден (поведение по умолчанию «без потерь»).
        var all = new FastDedup.Engine.FallbackEnumerator()
            .EnumerateFiles(_dir, new FastDedup.Engine.ScanOptions { Roots = new[] { _dir }, SkipHiddenSystem = false }, errors, default)
            .ToList();
        Assert.Contains(all, p => p.EndsWith("b.bin", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
