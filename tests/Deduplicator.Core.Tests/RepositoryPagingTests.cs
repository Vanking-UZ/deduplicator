using Deduplicator.Core.Data;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public sealed class RepositoryPagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dedup_pages_" + Guid.NewGuid().ToString("N"));

    public RepositoryPagingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Operation_log_keyset_pages_are_stable_and_complete()
    {
        var db = new Database(Path.Combine(_root, "journal.db"));
        db.Initialize();
        var log = new OperationLogRepository(db);
        for (int i = 0; i < 205; i++)
            log.Insert(new OperationLogEntry
            {
                OpType = OperationType.DeleteToRecycle,
                SrcPath = $"file-{i}.bin",
                FileName = $"file-{i}.bin",
                PerformedAt = DateTimeOffset.UtcNow.AddSeconds(i)
            });

        var first = log.GetPage(100);
        var second = log.GetPage(100, first.NextBeforeId);
        var third = log.GetPage(100, second.NextBeforeId);
        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id).ToList();

        Assert.Equal(205, first.TotalCount);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(100, second.Items.Count);
        Assert.Equal(5, third.Items.Count);
        Assert.Null(third.NextBeforeId);
        Assert.Equal(205, ids.Distinct().Count());
    }

    [Fact]
    public void Cross_volume_page_uses_one_stable_grouped_result()
    {
        var db = new Database(Path.Combine(_root, "cross.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long firstVolume = volumes.Upsert(new VolumeRecord { Serial = "PAGE-A", Label = "A" });
        long secondVolume = volumes.Upsert(new VolumeRecord { Serial = "PAGE-B", Label = "B" });
        var records = new List<FileRecord>();
        for (int i = 0; i < 120; i++)
        {
            var hash = BitConverter.GetBytes(i + 1);
            records.Add(File(firstVolume, $"a/{i}.bin", hash));
            records.Add(File(secondVolume, $"b/{i}.bin", hash));
        }
        // Совпадение только внутри одного диска не должно попасть на экран сравнения носителей.
        records.Add(File(firstVolume, "local/one.bin", new byte[] { 9, 9, 9, 9 }));
        records.Add(File(firstVolume, "local/two.bin", new byte[] { 9, 9, 9, 9 }));
        files.UpsertMetaBatch(records);

        var first = files.GetCrossVolumeDuplicatesPage(0, 50);
        var second = files.GetCrossVolumeDuplicatesPage(50, 50);
        var third = files.GetCrossVolumeDuplicatesPage(100, 50);
        var keys = first.Groups.Concat(second.Groups).Concat(third.Groups)
            .Select(g => Convert.ToHexString(g[0].FullHash!)).ToList();

        Assert.Equal(120, first.TotalGroups);
        Assert.Equal(50, first.Groups.Count);
        Assert.Equal(50, second.Groups.Count);
        Assert.Equal(20, third.Groups.Count);
        Assert.All(first.Groups.Concat(second.Groups).Concat(third.Groups),
            g => Assert.Equal(2, g.Select(x => x.VolumeId).Distinct().Count()));
        Assert.Equal(120, keys.Distinct().Count());
    }

    [Fact]
    public void Staged_deletes_preserve_previous_result_until_atomic_publish()
    {
        var db = new Database(Path.Combine(_root, "publish.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var sessions = new ScanSessionRepository(db);
        var groups = new GroupRepository(db);
        long volume = volumes.Upsert(new VolumeRecord { Serial = "PUB", Label = "disk" });
        var hash = new byte[] { 1, 2, 3, 4 };
        var first = File(volume, "first.bin", hash);
        var second = File(volume, "second.bin", hash);
        files.UpsertMetaBatch(new[] { first, second });

        long previousSession = sessions.Create("{}");
        groups.InsertGroups(new[]
        {
            new DuplicateGroup
            {
                MatchType = MatchType.Exact,
                Confidence = 1,
                RepresentativeId = first.Id,
                Members =
                {
                    new DuplicateMember { FileId = first.Id, Similarity = 1 },
                    new DuplicateMember { FileId = second.Id, Similarity = 1 }
                }
            }
        }, previousSession);
        sessions.Finish(previousSession, ScanStage.Completed);

        long cancelledSession = sessions.Create("{}");
        Assert.Equal(1, files.StageDeleteByVolumeAndPath(cancelledSession, volume, first.RelPath));
        files.BeginScope(cancelledSession);
        files.AddVolumeToScope(cancelledSession, volume, new[] { FileCategory.Other });
        using (var conn = db.Open())
        using (var count = conn.CreateCommand())
        {
            count.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM scan_scope_files WHERE scan_session_id=$sid),
                  (SELECT COUNT(*) FROM scan_scope_volumes WHERE scan_session_id=$sid),
                  (SELECT COUNT(*) FROM scan_scope_effective WHERE scan_session_id=$sid)
                """;
            count.Parameters.AddWithValue("$sid", cancelledSession);
            using var reader = count.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0)); // no-op том не копируется построчно
            Assert.Equal(1, reader.GetInt32(1)); // один компактный дескриптор категории
            Assert.Equal(1, reader.GetInt32(2)); // staged-файл уже не участвует в новом результате
        }
        Assert.NotNull(files.GetByVolumeAndPath(volume, first.RelPath));
        Assert.Equal(2, groups.GetGroups(MatchType.Exact, previousSession).Single().Members.Count);

        files.DiscardStagedDeletes(cancelledSession);
        Assert.NotNull(files.GetByVolumeAndPath(volume, first.RelPath));
        Assert.Equal(2, groups.GetGroups(MatchType.Exact, previousSession).Single().Members.Count);

        long completedSession = sessions.Create("{}");
        files.StageDeleteByVolumeAndPath(completedSession, volume, first.RelPath);
        Assert.Equal(1, files.PublishStagedDeletesAndCompleteSession(completedSession, 2, 0));
        Assert.Null(files.GetByVolumeAndPath(volume, first.RelPath));
        Assert.Equal(completedSession, sessions.GetLatestCompletedSessionId());
    }

    [Fact]
    public void Giant_size_bucket_queries_are_keyset_paged_and_only_return_partial_collisions()
    {
        var db = new Database(Path.Combine(_root, "bucket.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long volume = volumes.Upsert(new VolumeRecord { Serial = "BUCKET", Label = "disk" });
        var rows = new List<FileRecord>();
        for (int i = 0; i < 12; i++)
        {
            var row = File(volume, $"item-{i}.bin", BitConverter.GetBytes(1000 + i));
            row.PartialHash = i < 2 ? new byte[] { 1 } : i < 5 ? new byte[] { 2 } : BitConverter.GetBytes(100 + i);
            row.FullHash = null;
            row.ScanState = ScanState.Partial;
            rows.Add(row);
        }
        files.UpsertMetaBatch(rows);

        var stats = Assert.Single(files.GetDuplicateSizeCandidateCounts());
        Assert.Equal(12L, stats.Count);
        var firstPage = files.GetBySizePage(100, 0, 5);
        var secondPage = files.GetBySizePage(100, firstPage[^1].Id, 5);
        Assert.Equal(5, firstPage.Count);
        Assert.Equal(5, secondPage.Count);

        var collisionIds = new List<long>();
        long after = 0;
        while (true)
        {
            var page = files.GetFullHashCandidatesForSizePage(100, after, 2);
            if (page.Count == 0) break;
            collisionIds.AddRange(page.Select(x => x.Id));
            after = page[^1].Id;
        }
        Assert.Equal(5, collisionIds.Count);
        Assert.Equal(5, collisionIds.Distinct().Count());
    }

    [Fact]
    public void Volume_keyset_pages_are_complete_without_loading_the_whole_volume()
    {
        var db = new Database(Path.Combine(_root, "volume-pages.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long volume = volumes.Upsert(new VolumeRecord { Serial = "VOL-PAGE", Label = "disk" });
        files.UpsertMetaBatch(Enumerable.Range(0, 23)
            .Select(i => File(volume, $"folder/item-{i:D2}.bin", BitConverter.GetBytes(i + 1)))
            .ToList());

        var ids = new List<long>();
        long after = 0;
        while (true)
        {
            var page = files.GetByVolumePage(volume, after, 7);
            if (page.Count == 0) break;
            ids.AddRange(page.Select(f => f.Id));
            after = page[^1].Id;
        }

        Assert.Equal(23, ids.Count);
        Assert.Equal(23, ids.Distinct().Count());
        Assert.True(ids.SequenceEqual(ids.OrderBy(x => x)));
    }

    [Fact]
    public void Indexed_explorer_returns_only_direct_children_in_bounded_pages()
    {
        var db = new Database(Path.Combine(_root, "explorer-pages.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long volume = volumes.Upsert(new VolumeRecord { Serial = "EXP-PAGE", Label = "offline" });

        var rows = new List<FileRecord>
        {
            File(volume, @"Album\2024\a.jpg", new byte[] { 1 }),
            File(volume, "Album/2023/b.jpg", new byte[] { 2 }),
            File(volume, @"Docs\manual.pdf", new byte[] { 3 }),
            File(volume, "root-z.bin", new byte[] { 4 }),
            File(volume, "root-a.bin", new byte[] { 5 })
        };
        rows[^2].Size = 900;
        rows[^1].Size = 100;
        files.UpsertMetaBatch(rows);

        var root1 = files.GetDirectChildrenPage(volume, null, 0, 2);
        var root2 = files.GetDirectChildrenPage(volume, "", 2, 2);
        Assert.True(root1.HasMore);
        Assert.False(root2.HasMore);
        Assert.Equal(new[] { "Album", "Docs", "root-a.bin", "root-z.bin" },
            root1.Items.Concat(root2.Items).Select(x => x.Name));
        Assert.All(root1.Items, x => Assert.True(x.IsFolder));

        var album = files.GetDirectChildrenPage(volume, @"Album\", 0, 10);
        Assert.Equal(new[] { "2023", "2024" }, album.Items.Select(x => x.Name));
        Assert.All(album.Items, x => Assert.True(x.IsFolder));

        var year = files.GetDirectChildrenPage(volume, "Album/2024", 0, 10);
        var photo = Assert.Single(year.Items);
        Assert.False(photo.IsFolder);
        Assert.Equal("Album/2024/a.jpg", photo.RelPath);

        var folders = files.GetDirectChildrenPage(volume, null, 0, 10, foldersOnly: true);
        Assert.Equal(new[] { "Album", "Docs" }, folders.Items.Select(x => x.Name));

        var bySize = files.GetDirectChildrenPage(volume, null, 0, 10, sortField: "size", sortAscending: false);
        Assert.Equal(new[] { "Album", "Docs", "root-z.bin", "root-a.bin" }, bySize.Items.Select(x => x.Name));
    }

    [Fact]
    public void Consolidation_pages_keep_exact_groups_contiguous_across_page_boundaries()
    {
        var db = new Database(Path.Combine(_root, "consolidation-pages.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long firstVolume = volumes.Upsert(new VolumeRecord { Serial = "CONS-A", Label = "A" });
        long secondVolume = volumes.Upsert(new VolumeRecord { Serial = "CONS-B", Label = "B" });
        long excludedVolume = volumes.Upsert(new VolumeRecord { Serial = "CONS-X", Label = "X" });
        var same = new byte[] { 1, 2, 3, 4 };
        var rows = Enumerable.Range(0, 5)
            .Select(i => File(i % 2 == 0 ? firstVolume : secondVolume, $"same-{i}.bin", same))
            .ToList();
        rows.Add(File(firstVolume, "other.bin", new byte[] { 9, 9, 9, 9 }));
        rows.Add(File(firstVolume, "unhashed.bin", Array.Empty<byte>()));
        rows[^1].FullHash = null;
        rows.Add(File(excludedVolume, "excluded.bin", same));
        files.UpsertMetaBatch(rows);

        var selected = new[] { firstVolume, secondVolume };
        var hashed = new List<FileRecord>();
        byte[]? cursorHash = null;
        long afterId = 0;
        while (true)
        {
            var page = files.GetConsolidationPage(selected, true, afterId, cursorHash, 3);
            if (page.Count == 0) break;
            hashed.AddRange(page);
            cursorHash = page[^1].FullHash;
            afterId = page[^1].Id;
        }

        Assert.Equal(6, hashed.Count);
        Assert.Equal(5, hashed.Take(5).Count(f => f.FullHash!.SequenceEqual(same)));
        Assert.DoesNotContain(hashed, f => f.VolumeId == excludedVolume);

        var unhashed = files.GetConsolidationPage(selected, false, 0, null, 3);
        Assert.Equal("unhashed.bin", Assert.Single(unhashed).Name);
    }

    private static FileRecord File(long volumeId, string path, byte[] hash) => new()
    {
        VolumeId = volumeId,
        RelPath = path,
        Name = Path.GetFileName(path),
        Size = 100,
        Mtime = DateTimeOffset.UtcNow,
        Created = DateTimeOffset.UtcNow,
        Category = FileCategory.Other,
        FullHash = hash,
        ScanState = ScanState.Full
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
