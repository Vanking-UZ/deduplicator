using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>Сквозной тест: реальные файлы → слепки в БД → группировка точных дубликатов.</summary>
public class GrouperIntegrationTests : IDisposable
{
    private readonly string _dir;

    public GrouperIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Exact_duplicates_form_one_group_with_two_members()
    {
        // Два байт-идентичных файла + один отличающийся.
        var data = new byte[150_000];
        new Random(11).NextBytes(data);
        WriteFile("photo.jpg", data);
        WriteFile("copy_of_photo.jpg", (byte[])data.Clone());
        WriteFile("other.jpg", new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 });

        var db = new Database(Path.Combine(_dir, "it.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();

        var vol = new VolumeRecord { Serial = "ITVOL", Label = "Тест", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        var records = new List<FileRecord>();
        foreach (var path in Directory.GetFiles(_dir, "*.jpg"))
        {
            var size = new FileInfo(path).Length;
            records.Add(new FileRecord
            {
                VolumeId = vol.Id,
                RelPath = Path.GetFileName(path),
                Name = Path.GetFileName(path),
                Ext = ".jpg",
                Size = size,
                Mtime = DateTimeOffset.Now,
                Category = FileCategory.Image,
                PartialHash = hash.ComputePartialHash(path, size),
                FullHash = hash.ComputeFullHash(path),
                ScanState = ScanState.Full
            });
        }
        files.UpsertMetaBatch(records);

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(1, created);
        var result = groups.GetGroups(MatchType.Exact);
        Assert.Single(result);
        Assert.Equal(2, result[0].Members.Count);
        Assert.True(result[0].ReclaimableBytes > 0);
    }

    private void WriteFile(string name, byte[] data) =>
        File.WriteAllBytes(Path.Combine(_dir, name), data);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
