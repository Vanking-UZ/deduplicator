using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Tests;

/// <summary>
/// 3.7 «Лучший кадр серии»: метрика резкости (дисперсия Лапласиана), её расчёт конвейером
/// вместе с pHash (один декод), хранение в files.sharpness, выбор эталона похожей группы
/// по максимальной резкости и миграция старых баз без колонки sharpness.
/// </summary>
public class Sprint21SharpnessTests : IDisposable
{
    private readonly string _dir;

    public Sprint21SharpnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s21_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Шахматная доска — максимум резких границ (высокий отклик Лапласиана).</summary>
    private static Image<Rgba32> MakeCheckerboard(int size = 256, int cell = 8)
    {
        var img = new Image<Rgba32>(size, size);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte v = (x / cell + y / cell) % 2 == 0 ? (byte)255 : (byte)0;
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
        return img;
    }

    // === (а) Чистая метрика: резкое ЗАМЕТНО выше размытого, однотонное — ноль ===
    [Fact]
    public void Sharpness_checkerboard_far_above_blurred_and_flat_is_zero()
    {
        using var sharp = MakeCheckerboard();
        using var blurred = sharp.Clone(c => c.GaussianBlur(3f));
        using var flat = new Image<Rgba32>(256, 256, new Rgba32(128, 128, 128, 255));

        double sharpVal = ImageMatchService.ComputeSharpness(sharp);
        double blurredVal = ImageMatchService.ComputeSharpness(blurred);
        double flatVal = ImageMatchService.ComputeSharpness(flat);

        Assert.True(sharpVal > 0);
        // «Заметно выше»: с большим запасом над порогом заметности бейджа (1.15).
        Assert.True(sharpVal > blurredVal * 5,
            $"резкая {sharpVal:0.##} должна быть заметно выше размытой {blurredVal:0.##}");
        Assert.InRange(flatVal, 0, 1e-6); // нет границ — нет отклика
    }

    // === (а+) ComputeSignatures: pHash байт в байт как у ComputePHash, резкость с того же декода ===
    [Fact]
    public void ComputeSignatures_phash_matches_and_prefilter_yields_nulls()
    {
        var svc = new ImageMatchService(_dir, rotationInvariant: true);

        var p = Path.Combine(_dir, "sig.png");
        using (var img = MakeCheckerboard()) img.SaveAsPng(p);
        var sig = svc.ComputeSignatures(p);
        Assert.NotNull(sig.PHash);
        Assert.NotNull(sig.Sharpness);
        Assert.Equal(svc.ComputePHash(p), sig.PHash); // делегирование: тот же core, байт в байт
        Assert.Equal(sig.PHash, svc.ComputeSignatures(File.ReadAllBytes(p)).PHash); // и из памяти

        // Иконка меньше префильтра 64×64 и битое содержимое — оба признака null (пропуск, не исключение).
        var icon = Path.Combine(_dir, "icon.png");
        using (var img = MakeCheckerboard(32, 4)) img.SaveAsPng(icon);
        Assert.Equal(default, svc.ComputeSignatures(icon));
        Assert.Equal(default, svc.ComputeSignatures(new byte[] { 1, 2, 3 }));
    }

    // === (б) Конвейер: резкий и размытый файлы получают Sharpness в БД, у резкого больше ===
    [Fact]
    public void Pipeline_stores_sharpness_for_both_files_and_sharp_one_is_higher()
    {
        var sharpPath = Path.Combine(_dir, "sharp.png");
        var blurPath = Path.Combine(_dir, "blur.png");
        using (var img = MakeCheckerboard()) img.SaveAsPng(sharpPath);
        using (var img = MakeCheckerboard())
        {
            img.Mutate(c => c.GaussianBlur(3f));
            img.SaveAsPng(blurPath);
        }

        var db = new Database(Path.Combine(_dir, "pipe.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S21P", Label = "t" });

        var records = new[] { "sharp.png", "blur.png" }.Select(name => new FileRecord
        {
            VolumeId = vid, RelPath = name, Name = name, Ext = ".png",
            Size = new FileInfo(Path.Combine(_dir, name)).Length,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image
        }).ToList();
        files.UpsertMetaBatch(records);

        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        ImagePerceptualPipeline.Run(records, svc, new ImagePipelineOptions
        {
            PathFor = f => Path.Combine(_dir, f.RelPath),
            ReadDopFor = _ => 2,
            Budget = new PipelineBudget(128L * 1024 * 1024, 2),
            MaxVolumeParallelism = 1,
            ComputePixelHash = false
        }, CancellationToken.None);
        files.UpdateComputedBatch(records);

        var sharpRec = files.GetByVolumeAndPath(vid, "sharp.png")!;
        var blurRec = files.GetByVolumeAndPath(vid, "blur.png")!;
        Assert.NotNull(sharpRec.PHash);
        Assert.NotNull(sharpRec.Sharpness);
        Assert.NotNull(blurRec.Sharpness);
        Assert.True(sharpRec.Sharpness > blurRec.Sharpness,
            $"резкий {sharpRec.Sharpness:0.##} должен быть резче размытого {blurRec.Sharpness:0.##}");
    }

    // --- Хелперы для тестов группировки ---

    private (FileRepository Files, GroupRepository Groups, DuplicateGrouper Grouper, long Vid) MakeGrouperDb(string name)
    {
        var db = new Database(Path.Combine(_dir, name));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var grouper = new DuplicateGrouper(files, groups, new LshRepository(db), new HashService());
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S21G" + name, Label = "t" });
        return (files, groups, grouper, vid);
    }

    private static FileRecord ImageRec(long vid, string name, int ageDays, byte[] phash, double? sharpness) => new()
    {
        VolumeId = vid, RelPath = name, Name = name, Ext = ".jpg", Size = 1000,
        Mtime = DateTimeOffset.Now.AddDays(-ageDays), Category = FileCategory.Image,
        PHash = phash, PHashVer = ImageMatchService.CurrentPHashVersion, Sharpness = sharpness
    };

    // === (в) Эталон похожей группы — самый резкий кадр (не самый старый по правилу) ===
    [Fact]
    public void Similar_image_group_representative_is_sharpest_member()
    {
        var (files, groups, grouper, vid) = MakeGrouperDb("rep.db");
        var phash = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; // одинаковый pHash → одна группа
        var recs = new List<FileRecord>
        {
            ImageRec(vid, "a.jpg", ageDays: 30, phash, sharpness: 10.0),  // самый старый — фаворит правила Oldest
            ImageRec(vid, "b.jpg", ageDays: 20, phash, sharpness: 100.0), // самый резкий — должен стать эталоном
            ImageRec(vid, "c.jpg", ageDays: 10, phash, sharpness: null)   // старая строка без резкости
        };
        files.UpsertMetaBatch(recs);

        int created = grouper.BuildSimilarImageGroups(0.9, _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(1, created);
        var g = Assert.Single(groups.GetGroups(MatchType.SimilarImage));
        Assert.Equal(3, g.Members.Count);
        Assert.Equal(recs[1].Id, g.RepresentativeId); // резкость победила правило Oldest
    }

    // === (в+) Меньше двух членов с резкостью — прежнее правило (резкость не «выигрывает вакуумом») ===
    [Fact]
    public void Similar_image_group_falls_back_to_rule_when_sharpness_is_scarce()
    {
        var (files, groups, grouper, vid) = MakeGrouperDb("fallback.db");
        var phash = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        var recs = new List<FileRecord>
        {
            ImageRec(vid, "old.jpg", ageDays: 30, phash, sharpness: null),
            ImageRec(vid, "mid.jpg", ageDays: 20, phash, sharpness: 50.0), // резкость лишь у одного
            ImageRec(vid, "new.jpg", ageDays: 10, phash, sharpness: null)
        };
        files.UpsertMetaBatch(recs);

        grouper.BuildSimilarImageGroups(0.9, _ => true, RepresentativeRule.Oldest, null, default);

        var g = Assert.Single(groups.GetGroups(MatchType.SimilarImage));
        Assert.Equal(recs[0].Id, g.RepresentativeId); // правило Oldest, как раньше
    }

    // === (г) Миграция: старая строка с phash без sharpness не ломает выборки ===
    [Fact]
    public void Old_db_row_with_phash_and_no_sharpness_column_survives_migration()
    {
        var path = Path.Combine(_dir, "legacy.db");

        // «Старая» БД: схема без sharpness (и без прочих поздних колонок — их добавят миграции).
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = """
                CREATE TABLE volumes (id INTEGER PRIMARY KEY, label TEXT, serial TEXT, fs_type TEXT,
                    total_bytes INTEGER, first_seen TEXT, last_seen TEXT, last_scanned TEXT);
                CREATE TABLE files (id INTEGER PRIMARY KEY,
                    volume_id INTEGER NOT NULL REFERENCES volumes(id) ON DELETE CASCADE,
                    rel_path TEXT NOT NULL, name TEXT NOT NULL, ext TEXT, size INTEGER NOT NULL,
                    mtime TEXT NOT NULL, created TEXT, category TEXT,
                    partial_hash BLOB, full_hash BLOB, phash BLOB, pixel_hash BLOB, audio_fp BLOB,
                    scan_state TEXT, last_indexed TEXT, UNIQUE(volume_id, rel_path));
                INSERT INTO volumes (id, label, serial) VALUES (1, 'Disk', 'S21-LEGACY');
                INSERT INTO files (id, volume_id, rel_path, name, size, mtime, category, phash)
                    VALUES (10, 1, 'old.jpg', 'old.jpg', 5, '2020-01-01', 'image', x'0102030405060708');
                """;
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var db = new Database(path);
        db.Initialize(); // миграция добавляет колонку sharpness (NULL у старых строк)
        var files = new FileRepository(db);

        var rec = files.GetById(10);
        Assert.NotNull(rec);
        Assert.NotNull(rec!.PHash);
        Assert.Null(rec.Sharpness); // старая строка — без резкости, пересчёт не форсируется

        // Выборка группировщика похожих работает и включает строку без sharpness.
        var withPerceptual = files.GetByCategoryWithPerceptual(FileCategory.Image);
        Assert.Contains(withPerceptual, f => f.Id == 10);

        // Дозапись резкости обычным путём (пересчёт фото) — колонка мигрировала корректно.
        rec.Sharpness = 42.5;
        files.UpdateComputedBatch(new[] { rec });
        Assert.Equal(42.5, files.GetById(10)!.Sharpness);
    }

    // === (д) Upsert изменившегося файла сбрасывает sharpness вместе с pHash ===
    [Fact]
    public void Upsert_of_changed_file_resets_sharpness_like_phash()
    {
        var db = new Database(Path.Combine(_dir, "upsert.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S21U", Label = "t" });

        var first = ImageRec(vid, "x.jpg", ageDays: 5, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, sharpness: 77.0);
        files.UpsertMetaBatch(new[] { first });
        Assert.Equal(77.0, files.GetByVolumeAndPath(vid, "x.jpg")!.Sharpness);

        // Файл изменился: энумератор строит СВЕЖУЮ запись (хэши/резкость null) и делает Upsert.
        var changed = new FileRecord
        {
            VolumeId = vid, RelPath = "x.jpg", Name = "x.jpg", Ext = ".jpg", Size = 2000,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image
        };
        files.UpsertMetaBatch(new[] { changed });

        var back = files.GetByVolumeAndPath(vid, "x.jpg")!;
        Assert.Null(back.PHash);     // как раньше
        Assert.Null(back.Sharpness); // резкость сброшена тем же правилом, что и хэши
    }

    // === (е) Перенос с байт-идентичной копии: sharpness едет вместе с pHash ===
    [Fact]
    public void Propagate_transfers_sharpness_with_phash_to_byte_identical_copy()
    {
        var db = new Database(Path.Combine(_dir, "prop.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S21PR", Label = "t" });

        var fullHash = new byte[] { 42, 42, 42, 42 };
        var donor = ImageRec(vid, "donor.jpg", ageDays: 2, new byte[] { 1, 1, 2, 2, 3, 3, 4, 4 }, sharpness: 123.5);
        donor.FullHash = fullHash;
        var copy = new FileRecord
        {
            VolumeId = vid, RelPath = "copy.jpg", Name = "copy.jpg", Ext = ".jpg", Size = 1000,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image, FullHash = fullHash
        };
        files.UpsertMetaBatch(new[] { donor, copy });

        int filled = files.PropagatePerceptualByFullHash(FileCategory.Image);

        Assert.True(filled >= 1);
        var back = files.GetByVolumeAndPath(vid, "copy.jpg")!;
        Assert.Equal(donor.PHash, back.PHash);
        Assert.Equal(123.5, back.Sharpness); // без переноса копия навсегда осталась бы с NULL
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
