using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Deduplicator.Core.Tests;

/// <summary>
/// 3.5 «Консолидация с учётом похожих фото»: опция CollapseSimilarPhotos схлопывает почти
/// одинаковые кадры (консервативный порог 0.97 — не более 1 бита различия pHash) до одного
/// самого резкого; схлопнутые явно перечисляются в плане/отчёте и НЕ копируются, а исходники
/// не трогаются. Плюс 3.6: юниты на слим-выборку GetPerceptualSlimPage / дотяжку GetByIds и
/// проверка, что потоковая группировка даёт те же группы, что прежняя материализация.
/// </summary>
public class Sprint23ConsolidationTests : IDisposable
{
    private readonly string _dir;

    public Sprint23ConsolidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s23_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // ---------------- Инфраструктура ----------------

    /// <summary>Каталог томов без обращения к ОС: консолидации нужен только список с MountPath.</summary>
    private sealed class FakeVolumeService : IVolumeService
    {
        private readonly List<VolumeRecord> _catalog;
        public FakeVolumeService(params VolumeRecord[] vols) => _catalog = vols.ToList();
        public List<VolumeRecord> GetConnectedVolumes() => _catalog.Where(v => v.IsConnected).ToList();
        public List<VolumeRecord> SyncAndGetCatalog() => _catalog;
        public long Register(VolumeRecord volume) => volume.Id;
    }

    private (FileRepository Files, ConsolidationService Cons, VolumeRecord Vol, string SrcDir) MakeConsolidationEnv(string name)
    {
        var srcDir = Path.Combine(_dir, name + "_src");
        Directory.CreateDirectory(srcDir);
        var db = new Database(Path.Combine(_dir, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var vol = new VolumeRecord { Serial = "S23" + name, Label = "Тест", MountPath = srcDir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);
        var cons = new ConsolidationService(files, new FakeVolumeService(vol));
        return (files, cons, vol, srcDir);
    }

    /// <summary>Простая картинка; shift чуть меняет содержимое (байты файлов различаются).</summary>
    private static void SavePng(string path, byte shift)
    {
        using var img = new Image<Rgba32>(64, 64);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 4), (byte)(y * 4), (byte)(x + y + shift), 255);
            }
        });
        img.SaveAsPng(path);
    }

    /// <summary>Слепок фото: pHash/Sharpness проставляются прямо в БД (не зависим от порогов генерации).</summary>
    private static FileRecord PhotoRec(long vid, string name, ulong phash, double? sharpness, byte fullHashSeed,
        long size = 1000) => new()
    {
        VolumeId = vid, RelPath = name, Name = name, Ext = ".png", Size = size,
        Mtime = DateTimeOffset.Now, Created = DateTimeOffset.Now, Category = FileCategory.Image,
        FullHash = new byte[] { fullHashSeed, 11, 22, 33 },
        PHash = BitConverter.GetBytes(phash), Sharpness = sharpness, ScanState = ScanState.Full
    };

    private const ulong BaseHash = 0xF0F0_1234_5678_9ABC;

    // ---------------- 3.5: схлопывание похожих при консолидации ----------------

    [Fact]
    public void Collapse_option_keeps_sharpest_of_similar_pair_and_reports_collapsed()
    {
        var (files, cons, vol, src) = MakeConsolidationEnv("collapse");
        // Картинка, её чуть изменённая копия и непохожая третья — реальные файлы для копирования.
        SavePng(Path.Combine(src, "sharp.png"), 0);
        SavePng(Path.Combine(src, "blurry.png"), 3);
        SavePng(Path.Combine(src, "other.png"), 128);

        files.UpsertMetaBatch(new List<FileRecord>
        {
            PhotoRec(vol.Id, "sharp.png", BaseHash, sharpness: 120.0, fullHashSeed: 1),
            // 1 бит различия → похожесть 63/64 ≈ 0.984 ≥ 0.97: пара схлопывается.
            PhotoRec(vol.Id, "blurry.png", BaseHash ^ 1UL, sharpness: 15.0, fullHashSeed: 2),
            // Инверсия всех бит — заведомо непохожий кадр.
            PhotoRec(vol.Id, "other.png", ~BaseHash, sharpness: 80.0, fullHashSeed: 3)
        });

        var target = Path.Combine(_dir, "collapse_target");
        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { vol.Id },
            TargetPath = target,
            PreserveFolderStructure = false,
            CollapseSimilarPhotos = true
        };

        var plan = cons.BuildPlan(options);

        // В архив едут два файла: самый резкий из пары + непохожий.
        Assert.Equal(2, plan.UniqueCount);
        Assert.Contains(plan.ToCopy, i => i.File.Name == "sharp.png");
        Assert.Contains(plan.ToCopy, i => i.File.Name == "other.png");

        // План явно перечисляет схлопнутое: что не поехало и какой кадр оставлен.
        var collapsed = Assert.Single(plan.CollapsedSimilar);
        Assert.Equal("blurry.png", collapsed.File.Name);
        Assert.Equal("sharp.png", collapsed.CollapsedInto!.Name);
        Assert.False(collapsed.WillCopy);
        Assert.True(collapsed.SimilarityToKept >= 0.97);
        Assert.Equal(1, plan.CollapsedCount);

        var report = cons.Execute(plan, options, null, default);
        Assert.Equal(2, report.Copied);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1, report.CollapsedSimilar);
        Assert.Contains(report.CollapsedDetails, d => d.Contains("blurry.png") && d.Contains("sharp.png"));

        var copiedNames = Directory.GetFiles(target).Select(Path.GetFileName).ToList();
        Assert.Contains("sharp.png", copiedNames);
        Assert.Contains("other.png", copiedNames);
        Assert.DoesNotContain("blurry.png", copiedNames);

        // Схлопнутые оригиналы НЕ удаляются: консолидация вообще не трогает источники.
        Assert.True(File.Exists(Path.Combine(src, "blurry.png")));
        Assert.True(File.Exists(Path.Combine(src, "sharp.png")));
        Assert.True(File.Exists(Path.Combine(src, "other.png")));
    }

    [Fact]
    public void Without_option_all_three_files_are_copied()
    {
        var (files, cons, vol, src) = MakeConsolidationEnv("nocollapse");
        SavePng(Path.Combine(src, "sharp.png"), 0);
        SavePng(Path.Combine(src, "blurry.png"), 3);
        SavePng(Path.Combine(src, "other.png"), 128);

        files.UpsertMetaBatch(new List<FileRecord>
        {
            PhotoRec(vol.Id, "sharp.png", BaseHash, sharpness: 120.0, fullHashSeed: 1),
            PhotoRec(vol.Id, "blurry.png", BaseHash ^ 1UL, sharpness: 15.0, fullHashSeed: 2),
            PhotoRec(vol.Id, "other.png", ~BaseHash, sharpness: 80.0, fullHashSeed: 3)
        });

        var target = Path.Combine(_dir, "nocollapse_target");
        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { vol.Id },
            TargetPath = target,
            PreserveFolderStructure = false
            // CollapseSimilarPhotos по умолчанию FALSE — консервативно.
        };

        var plan = cons.BuildPlan(options);
        Assert.Equal(3, plan.UniqueCount);
        Assert.Empty(plan.CollapsedSimilar);

        var report = cons.Execute(plan, options, null, default);
        Assert.Equal(3, report.Copied);
        Assert.Equal(0, report.CollapsedSimilar);
        Assert.Empty(report.CollapsedDetails);
        Assert.Equal(3, Directory.GetFiles(target).Length);
    }

    [Fact]
    public void Pair_below_conservative_threshold_is_not_collapsed()
    {
        var (files, cons, vol, src) = MakeConsolidationEnv("threshold");
        SavePng(Path.Combine(src, "a.png"), 0);
        SavePng(Path.Combine(src, "b.png"), 3);

        files.UpsertMetaBatch(new List<FileRecord>
        {
            PhotoRec(vol.Id, "a.png", BaseHash, sharpness: 50.0, fullHashSeed: 1),
            // 2 бита различия → 62/64 ≈ 0.969 < 0.97: пользовательский порог поиска (0.90) такую
            // пару сгруппировал бы, но консервативная консолидация её НЕ схлопывает.
            PhotoRec(vol.Id, "b.png", BaseHash ^ 0b11UL, sharpness: 10.0, fullHashSeed: 2)
        });

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { vol.Id },
            TargetPath = Path.Combine(_dir, "threshold_target"),
            PreserveFolderStructure = false,
            CollapseSimilarPhotos = true
        };

        var plan = cons.BuildPlan(options);
        Assert.Equal(2, plan.UniqueCount);
        Assert.Empty(plan.CollapsedSimilar);
    }

    [Fact]
    public void Collapse_falls_back_to_bigger_file_when_no_sharpness()
    {
        var (files, cons, vol, src) = MakeConsolidationEnv("fallback");
        SavePng(Path.Combine(src, "big.png"), 0);
        SavePng(Path.Combine(src, "small.png"), 3);

        files.UpsertMetaBatch(new List<FileRecord>
        {
            // Резкость не посчитана ни у кого (старые строки) — оставляем больший файл.
            PhotoRec(vol.Id, "small.png", BaseHash, sharpness: null, fullHashSeed: 1, size: 1000),
            PhotoRec(vol.Id, "big.png", BaseHash ^ 1UL, sharpness: null, fullHashSeed: 2, size: 5000)
        });

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = { vol.Id },
            TargetPath = Path.Combine(_dir, "fallback_target"),
            PreserveFolderStructure = false,
            CollapseSimilarPhotos = true
        };

        var plan = cons.BuildPlan(options);
        Assert.Equal("big.png", Assert.Single(plan.ToCopy).File.Name);
        var collapsed = Assert.Single(plan.CollapsedSimilar);
        Assert.Equal("small.png", collapsed.File.Name);
        Assert.Equal("big.png", collapsed.CollapsedInto!.Name);
    }

    [Fact]
    public void Collapse_scales_past_twenty_thousand_candidates_without_changing_semantics()
    {
        var (files, cons, vol, _) = MakeConsolidationEnv("largecollapse");
        const int count = 20_001;
        var records = new List<FileRecord>(count);
        for (int i = 0; i < count; i++)
        {
            var record = PhotoRec(vol.Id, $"photo-{i:D5}.png", BaseHash, sharpness: i,
                fullHashSeed: 1, size: 1_000 + i);
            // Все содержательно уникальны (exact-групп нет), но имеют одинаковый pHash.
            record.FullHash = BitConverter.GetBytes(i + 1L);
            records.Add(record);
        }
        files.UpsertMetaBatch(records);

        var plan = cons.BuildPlan(new ConsolidationOptions
        {
            SourceVolumeIds = { vol.Id },
            TargetPath = Path.Combine(_dir, "largecollapse_target"),
            CollapseSimilarPhotos = true
        });

        var kept = Assert.Single(plan.ToCopy);
        Assert.Equal("photo-20000.png", kept.File.Name); // максимальная Sharpness
        Assert.Equal(count - 1, plan.CollapsedSimilar.Count);
    }

    // ---------------- 3.6: слим-выборка и дотяжка ----------------

    private (FileRepository Files, long Vid, Database Db) MakeRepoEnv(string name)
    {
        var db = new Database(Path.Combine(_dir, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S23R" + name, Label = "t" });
        return (files, vid, db);
    }

    [Fact]
    public void GetPerceptualSlimPage_pages_by_id_and_returns_only_slim_fields()
    {
        var (files, vid, _) = MakeRepoEnv("slim");
        var recs = new List<FileRecord>();
        for (int i = 0; i < 3; i++)
        {
            var r = PhotoRec(vid, $"img{i}.png", BaseHash + (ulong)i, sharpness: 5.0 + i, fullHashSeed: (byte)(i + 1),
                size: 100 + i);
            r.PartialHash = new byte[] { 7, 7, 7 };
            r.PixelHash = new byte[] { 9, 9, (byte)i };
            recs.Add(r);
        }
        // Изображение без pHash и аудио — в слим-выборку попадать не должны.
        recs.Add(new FileRecord
        {
            VolumeId = vid, RelPath = "nophash.png", Name = "nophash.png", Size = 1,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image
        });
        recs.Add(new FileRecord
        {
            VolumeId = vid, RelPath = "track.mp3", Name = "track.mp3", Size = 1,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Audio, AudioFp = new byte[] { 1, 2, 3, 4 }
        });
        files.UpsertMetaBatch(recs);

        // Keyset-пагинация: страница 2 + страница 1 + пустой конец.
        var page1 = files.GetPerceptualSlimPage(FileCategory.Image, 0, 2);
        Assert.Equal(2, page1.Count);
        var page2 = files.GetPerceptualSlimPage(FileCategory.Image, page1[^1].Id, 2);
        var only = Assert.Single(page2);
        Assert.Empty(files.GetPerceptualSlimPage(FileCategory.Image, only.Id, 2));

        var all = page1.Concat(page2).OrderBy(f => f.Id).ToList();
        Assert.Equal(recs.Take(3).Select(r => r.Id).OrderBy(x => x), all.Select(f => f.Id));

        // В слим-записи есть всё для предикатов inScope/профиля и признаки — и НЕТ тяжёлого.
        foreach (var slim in all)
        {
            var full = files.GetById(slim.Id)!;
            Assert.Equal(full.VolumeId, slim.VolumeId);
            Assert.Equal(full.RelPath, slim.RelPath);
            Assert.Equal(full.Size, slim.Size);
            Assert.Equal(full.Mtime, slim.Mtime);
            Assert.Equal(full.Created, slim.Created);
            Assert.Equal(full.Category, slim.Category);
            Assert.Equal(full.PHash, slim.PHash);
            Assert.Equal(full.PixelHash, slim.PixelHash);
            // Сознательно не выбирается (см. GetPerceptualSlimPage): имя и блобы хэшей содержимого.
            Assert.Equal(string.Empty, slim.Name);
            Assert.Null(slim.PartialHash);
            Assert.Null(slim.FullHash);
            Assert.Null(slim.VerifiedFullHash);
            Assert.Null(slim.AudioFp);
            Assert.Null(slim.Sharpness);
        }

        // Аудио-ветка не стримится — защита от неверного использования.
        Assert.Throws<ArgumentException>(() => files.GetPerceptualSlimPage(FileCategory.Audio, 0, 10));

        // Scope-aware: с областью сессии выбираются только её файлы.
        const long sid = 77;
        files.AddScope(sid, new[] { recs[1].Id });
        var scoped = files.GetPerceptualSlimPage(FileCategory.Image, 0, 10, sid);
        Assert.Equal(recs[1].Id, Assert.Single(scoped).Id);
    }

    [Fact]
    public void GetByIds_fetches_full_records_across_chunk_boundary()
    {
        var (files, vid, _) = MakeRepoEnv("byids");
        var recs = new List<FileRecord>();
        for (int i = 0; i < 450; i++)
            recs.Add(PhotoRec(vid, $"f{i:D4}.png", BaseHash + (ulong)i, sharpness: i, fullHashSeed: (byte)(i % 251),
                size: 10 + i));
        files.UpsertMetaBatch(recs);

        // 405 id (больше IN-чанка 400) + заведомо отсутствующий — он молча пропускается.
        var wanted = recs.Take(405).Select(r => r.Id).ToList();
        var ids = new List<long>(wanted) { 9_999_999 };
        var got = files.GetByIds(ids);

        Assert.Equal(405, got.Count);
        Assert.Equal(wanted.OrderBy(x => x), got.Select(f => f.Id).OrderBy(x => x));
        // Дотяжка возвращает ПОЛНЫЕ записи (в отличие от слим-выборки).
        var sample = got.First(f => f.Id == wanted[0]);
        Assert.Equal("f0000.png", sample.Name);
        Assert.NotNull(sample.FullHash);
        Assert.NotNull(sample.Sharpness);

        Assert.Empty(files.GetByIds(Array.Empty<long>()));
    }

    /// <summary>
    /// Семантика потоковой группировки (3.6) совпадает с прежней материализацией: на корпусе из
    /// 20 картинок группы равны эталонному расчёту теми же правилами (полный попарный перебор по
    /// pHash с порогом + объединение по одинаковому pixel_hash — при N&lt;=FullPairwiseCap прежний
    /// код делал ровно это).
    /// </summary>
    [Fact]
    public void Streaming_grouping_matches_reference_pairwise_semantics_on_20_images()
    {
        var (files, vid, db) = MakeRepoEnv("stream");
        var groups = new GroupRepository(db);
        var grouper = new DuplicateGrouper(files, groups, new LshRepository(db), new HashService());

        var rnd = new Random(42);
        ulong RndHash() => unchecked(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next());

        var recs = new List<FileRecord>();
        for (int i = 0; i < 14; i++) // «шум»: случайные хэши
            recs.Add(PhotoRec(vid, $"n{i:D2}.jpg", RndHash(), sharpness: i % 3 == 0 ? 10.0 + i : null,
                fullHashSeed: (byte)(i + 1), size: 100 + i));
        // Пара с одинаковым pHash + сосед в 3 битах (порог 0.9 допускает до 6 бит).
        recs.Add(PhotoRec(vid, "same1.jpg", BaseHash, sharpness: 70.0, fullHashSeed: 101));
        recs.Add(PhotoRec(vid, "same2.jpg", BaseHash, sharpness: 90.0, fullHashSeed: 102));
        recs.Add(PhotoRec(vid, "near.jpg", BaseHash ^ 0b111UL, sharpness: 30.0, fullHashSeed: 103));
        // Пара с далёкими pHash, но одинаковым pixel_hash (одна картинка в разных форматах).
        var pix = new byte[] { 4, 8, 15, 16, 23, 42 };
        var far1 = PhotoRec(vid, "pix1.jpg", 0x0000_0000_FFFF_0000UL, sharpness: null, fullHashSeed: 104);
        far1.PixelHash = pix;
        var far2 = PhotoRec(vid, "pix2.png", 0xFFFF_FFFF_0000_FFFFUL, sharpness: null, fullHashSeed: 105);
        far2.PixelHash = (byte[])pix.Clone();
        recs.Add(far1);
        recs.Add(far2);
        recs.Add(PhotoRec(vid, "loner.jpg", RndHash(), sharpness: 1.0, fullHashSeed: 106));
        Assert.Equal(20, recs.Count);
        files.UpsertMetaBatch(recs);

        const double threshold = 0.9;

        // Эталон: те же правила объединения, что у прежней (материализующей) группировки.
        var reference = new Dsu();
        foreach (var r in recs) reference.MakeSet(r.Id);
        for (int i = 0; i < recs.Count; i++)
        {
            for (int j = i + 1; j < recs.Count; j++)
            {
                var a = recs[i];
                var b = recs[j];
                if (a.PixelHash is not null && b.PixelHash is not null && a.PixelHash.SequenceEqual(b.PixelHash))
                    reference.Union(a.Id, b.Id);
                if (PerceptualLsh.Similarity(PerceptualLsh.ToHash(a.PHash), PerceptualLsh.ToHash(b.PHash)) >= threshold)
                    reference.Union(a.Id, b.Id);
            }
        }
        var expected = reference.Groups().Values
            .Where(g => g.Count >= 2)
            .Select(g => string.Join(",", g.OrderBy(x => x)))
            .OrderBy(s => s)
            .ToList();

        int created = grouper.BuildSimilarImageGroups(threshold, _ => true, RepresentativeRule.Oldest, null, default);

        var actualGroups = groups.GetGroups(MatchType.SimilarImage);
        var actual = actualGroups
            .Select(g => string.Join(",", g.Members.Select(m => m.FileId).OrderBy(x => x)))
            .OrderBy(s => s)
            .ToList();

        Assert.Equal(expected.Count, created);
        Assert.Equal(expected, actual);

        // Дотяжка полных записей: у членов групп есть имена и Sharpness-эталон работает (3.7):
        // в группе same1/same2/near самый резкий — same2.
        var sameGroup = actualGroups.Single(g => g.Members.Any(m => m.File!.Name == "same1.jpg"));
        Assert.Equal("same2.jpg", sameGroup.Members.Single(m => m.FileId == sameGroup.RepresentativeId).File!.Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
