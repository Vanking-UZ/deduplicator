using Deduplicator.Core.Data;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>Постраничная выборка файлов под перцептивный анализ (не грузим всё в память).</summary>
public class Sprint5PerceptualTests : IDisposable
{
    private readonly string _dir;

    public Sprint5PerceptualTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void GetNeedingPerceptualPage_pages_unprocessed_images_in_id_order()
    {
        var db = new Database(Path.Combine(_dir, "p.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var vol = new VolumeRecord { Serial = "P5", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        var recs = new List<FileRecord>();
        for (int i = 0; i < 5; i++)
            recs.Add(new FileRecord
            {
                VolumeId = vol.Id, RelPath = $"img{i}.jpg", Name = $"img{i}.jpg", Ext = ".jpg",
                Size = 100 + i, Mtime = DateTimeOffset.Now, Category = FileCategory.Image, ScanState = ScanState.Meta
            });
        // один аудиофайл — не должен попадать в выборку изображений
        recs.Add(new FileRecord
        {
            VolumeId = vol.Id, RelPath = "song.mp3", Name = "song.mp3", Ext = ".mp3",
            Size = 999, Mtime = DateTimeOffset.Now, Category = FileCategory.Audio, ScanState = ScanState.Meta
        });
        files.UpsertMetaBatch(recs);

        Assert.Equal(5, files.CountNeedingPerceptual(FileCategory.Image));

        // Постранично по 2: собираем все id, проверяем порядок и полноту.
        var collected = new List<long>();
        long after = 0;
        while (true)
        {
            var page = files.GetNeedingPerceptualPage(FileCategory.Image, after, 2);
            if (page.Count == 0) break;
            Assert.True(page.Count <= 2);
            Assert.All(page, f => Assert.Equal(FileCategory.Image, f.Category));
            collected.AddRange(page.Select(f => f.Id));
            after = page[^1].Id;
        }
        Assert.Equal(5, collected.Count);
        Assert.Equal(collected.OrderBy(x => x).ToList(), collected);

        // После проставления phash (текущей версии — этап 2.2) файл выбывает из выборки.
        var one = files.GetById(collected[0])!;
        one.PHash = new byte[] { 1, 2, 3 };
        one.PHashVer = Deduplicator.Core.Imaging.ImageMatchService.CurrentPHashVersion;
        files.UpdateComputedBatch(new[] { one });
        Assert.Equal(4, files.CountNeedingPerceptual(FileCategory.Image));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
