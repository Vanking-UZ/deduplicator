using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>Rename→DB-консистентность (P0.1) и чтение только последней завершённой сессии (P0.2).</summary>
public class Sprint10Projact5Tests : IDisposable
{
    private readonly string _dir;
    private readonly Database _db;
    private readonly VolumeRepository _volumes;
    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly ScanSessionRepository _sessions;
    private readonly long _volId;

    public Sprint10Projact5Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_p5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "p5.db"));
        _db.Initialize();
        _volumes = new VolumeRepository(_db);
        _files = new FileRepository(_db);
        _groups = new GroupRepository(_db);
        _sessions = new ScanSessionRepository(_db);
        var v = new VolumeRecord { Serial = "S5", MountPath = _dir, IsConnected = true };
        _volId = _volumes.Upsert(v);
    }

    private FileRecord IndexFile(string rel, long size, byte[]? fullHash = null)
    {
        var rec = new FileRecord
        {
            VolumeId = _volId, RelPath = rel, Name = Path.GetFileName(rel), Ext = Path.GetExtension(rel),
            Size = size, Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now,
            Category = FileCategory.Other, FullHash = fullHash, ScanState = ScanState.Full
        };
        _files.UpsertMetaBatch(new[] { rec });
        return rec;
    }

    [Fact]
    public void UpdatePathAfterRename_changes_path_and_keeps_hash()
    {
        var hash = new byte[] { 1, 2, 3, 4 };
        var rec = IndexFile("old/IMG_1.jpg", 1234, hash);

        _files.UpdatePathAfterRename(rec.Id, _volId, "new/Отпуск_001.jpg", "Отпуск_001.jpg", ".jpg",
            DateTimeOffset.Now, DateTimeOffset.Now, 1234);

        var after = _files.GetById(rec.Id)!;
        Assert.Equal("new/Отпуск_001.jpg", after.RelPath);
        Assert.Equal("Отпуск_001.jpg", after.Name);
        Assert.Equal(".jpg", after.Ext);
        Assert.Equal(hash, after.FullHash); // содержимое не менялось → хэш сохранён
    }

    private DuplicateGroup ExactGroup(FileRecord a, FileRecord b) => new()
    {
        MatchType = MatchType.Exact, Confidence = 1.0, RepresentativeId = a.Id, ReclaimableBytes = a.Size,
        Members = { new DuplicateMember { FileId = a.Id }, new DuplicateMember { FileId = b.Id } }
    };

    [Fact]
    public void Results_read_only_latest_completed_session()
    {
        // Сессия A — успешно завершена, со своими группами.
        long a = _sessions.Create("{}");
        var a1 = IndexFile("a1.bin", 10); var a2 = IndexFile("a2.bin", 10);
        _groups.InsertGroups(new[] { ExactGroup(a1, a2) }, a);
        _sessions.Finish(a, ScanStage.Completed);

        // Сессия B — началась и НЕ завершилась (как при краше), но успела записать частичную группу.
        long b = _sessions.Create("{}");
        var b1 = IndexFile("b1.bin", 20); var b2 = IndexFile("b2.bin", 20);
        _groups.InsertGroups(new[] { ExactGroup(b1, b2) }, b);

        Assert.Equal(a, _sessions.GetLatestCompletedSessionId()); // последняя ЗАВЕРШЁННАЯ — A

        var sid = _sessions.GetLatestCompletedSessionId();
        var shown = _groups.GetGroups(MatchType.Exact, sid)
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("a1.bin", shown);
        Assert.DoesNotContain("b1.bin", shown); // частичная группа сессии B не показывается
    }

    [Fact]
    public void Startup_cleanup_removes_partial_and_marks_unfinished_failed()
    {
        long a = _sessions.Create("{}");
        var a1 = IndexFile("a1.bin", 10); var a2 = IndexFile("a2.bin", 10);
        _groups.InsertGroups(new[] { ExactGroup(a1, a2) }, a);
        _sessions.Finish(a, ScanStage.Completed);

        long b = _sessions.Create("{}"); // в полёте (Enumerating), не завершена
        var b1 = IndexFile("b1.bin", 20); var b2 = IndexFile("b2.bin", 20);
        _groups.InsertGroups(new[] { ExactGroup(b1, b2) }, b);

        // Эмуляция старта приложения после краша.
        _sessions.MarkUnfinishedAsFailed();
        _groups.ClearGroupsNotInCompletedSessions();

        Assert.Null(_sessions.GetUnfinished());                  // незавершённых больше нет
        var remaining = _groups.GetGroups(MatchType.Exact)       // без фильтра — но осталась только A
            .SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("a1.bin", remaining);
        Assert.DoesNotContain("b1.bin", remaining); // частичные группы B удалены
    }

    [Fact]
    public void UpdatePathAfterRename_clears_stale_row_on_target_path()
    {
        var moved = IndexFile("a.bin", 10);   // переименуем в new.bin
        var stale = IndexFile("new.bin", 99); // устаревшая строка уже занимает целевой путь

        // Не должно упасть на UNIQUE(volume_id, rel_path) — устаревшую строку снимаем в той же транзакции.
        _files.UpdatePathAfterRename(moved.Id, _volId, "new.bin", "new.bin", ".bin",
            DateTimeOffset.Now, DateTimeOffset.Now, 10);

        Assert.Equal("new.bin", _files.GetById(moved.Id)!.RelPath);
        Assert.Null(_files.GetById(stale.Id)); // устаревшая строка удалена → индекс консистентен
    }

    [Fact]
    public void GetGroupsPage_pages_groups_with_members_and_counts()
    {
        const long sid = 7;
        for (int i = 0; i < 3; i++)
        {
            var a = IndexFile($"g{i}a.bin", 10 + i);
            var b = IndexFile($"g{i}b.bin", 10 + i);
            _groups.InsertGroups(new[] { ExactGroup(a, b) }, sid);
        }
        var types = new[] { MatchType.Exact };
        Assert.Equal(3, _groups.GetGroupCount(types, sid));

        var p1 = _groups.GetGroupsPage(types, sid, 0, 2);
        Assert.Equal(2, p1.Count);
        Assert.All(p1, g => Assert.Equal(2, g.Members.Count)); // члены подгружены для страницы
        Assert.All(p1, g => Assert.All(g.Members, m => Assert.NotNull(m.File)));

        var p2 = _groups.GetGroupsPage(types, sid, 2, 2);
        Assert.Single(p2); // последняя неполная страница
    }

    [Fact]
    public void Scope_limits_size_candidates_to_session_files()
    {
        var a = IndexFile("a.bin", 100); var b = IndexFile("b.bin", 100); // в области
        IndexFile("x.bin", 100); IndexFile("y.bin", 100);                  // вне области
        const long sid = 42;
        _files.ClearScope(sid);
        _files.AddScope(sid, new[] { a.Id, b.Id });

        Assert.Contains(100L, _files.GetDuplicateSizeCandidates(sid));
        Assert.Equal(2, _files.CountFilesWithDuplicateSizes(sid));          // только a,b
        Assert.Equal(2, _files.GetBySizesOrdered(new[] { 100L }, sid).Count); // только a,b
        Assert.Equal(4, _files.CountFilesWithDuplicateSizes());             // глобально все 4
    }

    [Fact]
    public void Missing_scope_marker_falls_back_to_global_for_legacy_calls()
    {
        // Обратная совместимость: произвольный sessionId без BeginScope означает старый глобальный вызов.
        // Явная пустая область проверяется отдельным тестом ниже.
        IndexFile("a.bin", 50); IndexFile("b.bin", 50);
        Assert.Equal(2, _files.CountFilesWithDuplicateSizes(99));
        Assert.Contains(50L, _files.GetDuplicateSizeCandidates(99));
        Assert.Equal(2, _files.GetBySizesOrdered(new[] { 50L }, 99).Count);
    }

    [Fact]
    public void Explicit_empty_scope_stays_empty_and_never_leaks_global_rows()
    {
        var a = IndexFile("a.bin", 50); var b = IndexFile("b.bin", 50);
        a.FullHash = b.FullHash = new byte[] { 1, 2, 3, 4 };
        _files.UpdateComputedBatch(new[] { a, b });

        const long sid = 100;
        _files.BeginScope(sid);

        Assert.Empty(_files.GetDuplicateSizeCandidates(sid));
        Assert.Equal(0, _files.CountFilesWithDuplicateSizes(sid));
        Assert.Empty(_files.GetBySizesOrdered(new[] { 50L }, sid));
        Assert.Empty(_files.GetSharedFullHashes(sid));
        Assert.Empty(_files.GetByFullHashes(new[] { a.FullHash! }, sid));
    }

    [Fact]
    public void Scoped_full_hash_queries_do_not_read_files_outside_session()
    {
        var a = IndexFile("a.bin", 50); var b = IndexFile("b.bin", 50);
        var x = IndexFile("x.bin", 50); var y = IndexFile("y.bin", 50);
        var hash = new byte[] { 9, 8, 7, 6 };
        foreach (var file in new[] { a, b, x, y }) file.FullHash = hash;
        _files.UpdateComputedBatch(new[] { a, b, x, y });

        const long sid = 101;
        _files.AddScope(sid, new[] { a.Id, b.Id });

        Assert.Single(_files.GetSharedFullHashes(sid));
        var rows = _files.GetByFullHashes(new[] { hash }, sid);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, f => f.Id == a.Id);
        Assert.Contains(rows, f => f.Id == b.Id);
        Assert.DoesNotContain(rows, f => f.Id == x.Id || f.Id == y.Id);
    }

    [Fact]
    public void Volume_scope_is_built_in_sql_and_filters_categories()
    {
        var image = IndexFile("photo.jpg", 100);
        image.Category = FileCategory.Image;
        var document = IndexFile("note.pdf", 100);
        document.Category = FileCategory.Document;
        var other = IndexFile("blob.bin", 100);
        other.Category = FileCategory.Other;
        _files.UpsertMetaBatch(new[] { image, document, other });

        const long sid = 102;
        _files.AddVolumeToScope(sid, image.VolumeId,
            new[] { FileCategory.Image, FileCategory.Document });

        var rows = _files.GetBySizesOrdered(new[] { 100L }, sid);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, f => f.Category == FileCategory.Image);
        Assert.Contains(rows, f => f.Category == FileCategory.Document);
        Assert.DoesNotContain(rows, f => f.Category == FileCategory.Other);
    }

    [Fact]
    public void Exact_grouper_uses_session_scope_even_when_predicate_is_broad()
    {
        var a = IndexFile("a.bin", 50); var b = IndexFile("b.bin", 50);
        var x = IndexFile("x.bin", 50); var y = IndexFile("y.bin", 50);
        var hash = new byte[] { 7, 7, 7, 7 };
        foreach (var file in new[] { a, b, x, y })
            file.FullHash = hash;
        _files.UpdateComputedBatch(new[] { a, b, x, y });

        const long sid = 102;
        _files.AddScope(sid, new[] { a.Id, b.Id });
        var grouper = new DuplicateGrouper(_files, _groups, new LshRepository(_db), new HashService());
        var volumes = new Dictionary<long, VolumeRecord>
        {
            [_volId] = new() { Id = _volId, IsConnected = false }
        };

        Assert.Equal(1, grouper.BuildExactGroups(
            volumes, _ => true, RepresentativeRule.Oldest, null, default, sessionId: sid));
        var members = _groups.GetGroups(MatchType.Exact, sid).Single().Members.Select(m => m.FileId).ToList();
        Assert.Equal(2, members.Count);
        Assert.Contains(a.Id, members);
        Assert.Contains(b.Id, members);
        Assert.DoesNotContain(x.Id, members);
        Assert.DoesNotContain(y.Id, members);
    }

    [Fact]
    public void PartialHash_is_deterministic_and_head_tail_sensitive()
    {
        var svc = new HashService();
        var data = new byte[200_000];
        new Random(1).NextBytes(data);
        var p1 = Path.Combine(_dir, "h1.bin"); File.WriteAllBytes(p1, data);
        var h1 = svc.ComputePartialHash(p1, data.Length);
        Assert.Equal(h1, svc.ComputePartialHash(p1, data.Length)); // детерминирован

        // Изменение ХВОСТА → другой хэш (хвост читается по абсолютному смещению).
        var tail = (byte[])data.Clone(); tail[^1] ^= 0xFF;
        var p2 = Path.Combine(_dir, "h2.bin"); File.WriteAllBytes(p2, tail);
        Assert.NotEqual(h1, svc.ComputePartialHash(p2, tail.Length));

        // Изменение ГОЛОВЫ → другой хэш.
        var head = (byte[])data.Clone(); head[0] ^= 0xFF;
        var p3 = Path.Combine(_dir, "h3.bin"); File.WriteAllBytes(p3, head);
        Assert.NotEqual(h1, svc.ComputePartialHash(p3, head.Length));
    }

    [Fact]
    public void Network_volume_is_read_sequentially()
    {
        Assert.True(new VolumeRecord { IsNetwork = true }.PreferSequentialReads);   // сеть → последовательно
        Assert.False(new VolumeRecord().PreferSequentialReads);                     // обычный том → параллельно
        Assert.Contains("сетевой", new VolumeRecord { IsNetwork = true, Label = "Z:" }.DisplayName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
