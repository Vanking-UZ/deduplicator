using Deduplicator.Core.Data;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Этап 2.2 (пакет Imaging): декод pHash сразу в TargetSize, потоковый pixel-hash,
/// префильтр иконок и версионирование pHash (files.phash_ver).
/// </summary>
public class Sprint13ImagingTests : IDisposable
{
    private readonly string _dir;

    public Sprint13ImagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s13_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Градиентная картинка (как в ImageHashTests, но свой узор — не полотно одного цвета).</summary>
    private static Image<Rgba32> MakeImage(int w, int h)
    {
        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 3), (byte)(y * 5), (byte)(x ^ y), 255);
            }
        });
        return img;
    }

    // === (а) rotation-invariant: pHash повёрнутой копии равен оригиналу (на TargetSize-декоде) ===
    [Fact]
    public void RotationInvariant_phash_equal_for_rotated_copy()
    {
        var a = Path.Combine(_dir, "rot_a.png");
        var b = Path.Combine(_dir, "rot_b.png");
        using (var img = MakeImage(200, 160))
        {
            img.SaveAsPng(a);
            using var r = img.Clone(c => c.Rotate(90f));
            r.SaveAsPng(b);
        }

        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        var ha = svc.ComputePHash(a);
        var hb = svc.ComputePHash(b);
        Assert.NotNull(ha);
        Assert.Equal(ha, hb); // канонический минимум по 8 ориентациям — на ужатой копии
    }

    // === (б) pixel-hash: одинаковые пиксели при разных байтах файла → равный хэш ===
    [Fact]
    public void PixelHash_equal_for_same_pixels_different_bytes_and_differs_for_other_pixels()
    {
        var fast = Path.Combine(_dir, "px_fast.png");
        var best = Path.Combine(_dir, "px_best.png");
        var other = Path.Combine(_dir, "px_other.png");
        using (var img = MakeImage(120, 90))
        {
            // Один и тот же кадр с разным уровнем сжатия PNG: пиксели те же, байты файла — разные.
            img.SaveAsPng(fast, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
            img.SaveAsPng(best, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
        }
        using (var img2 = MakeImage(120, 90))
        {
            img2.Mutate(c => c.Invert()); // другие пиксели
            img2.SaveAsPng(other);
        }
        Assert.NotEqual(File.ReadAllBytes(fast), File.ReadAllBytes(best)); // файлы действительно байт-разные

        var svc = new ImageMatchService(_dir);
        var h1 = svc.ComputePixelHash(fast);
        var h2 = svc.ComputePixelHash(best);
        var h3 = svc.ComputePixelHash(other);
        Assert.NotNull(h1);
        Assert.Equal(h1, h2);    // FR-14: «одинаковые пиксели, разные метаданные/кодирование»
        Assert.NotEqual(h1, h3); // разные пиксели — разный хэш
    }

    // === (в) префильтр: иконка меньше 64×64 по меньшей стороне не получает pHash ===
    [Fact]
    public void ComputePHash_returns_null_for_tiny_icon()
    {
        var icon = Path.Combine(_dir, "icon.png");
        using (var img = MakeImage(32, 32)) img.SaveAsPng(icon);

        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        Assert.Null(svc.ComputePHash(icon));

        // Граница: 64 по меньшей стороне — уже считается.
        var ok = Path.Combine(_dir, "ok.png");
        using (var img = MakeImage(64, 100)) img.SaveAsPng(ok);
        Assert.NotNull(svc.ComputePHash(ok));
    }

    // === (г) миграция: старая строка с phash (phash_ver=1) попадает в выборку needing-perceptual ===
    [Fact]
    public void Legacy_row_with_old_phash_version_is_selected_for_recompute()
    {
        var path = Path.Combine(_dir, "legacy.db");

        // «Старая» БД: схема files ещё БЕЗ колонок partial_ver/phash_ver, у строки уже есть phash (v1).
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
                INSERT INTO volumes (id, label, serial) VALUES (1, 'Disk', 'S13-MIG');
                INSERT INTO files (id, volume_id, rel_path, name, size, mtime, category, phash)
                    VALUES (10, 1, 'old.jpg', 'old.jpg', 100, '2020-01-01', 'image', x'0102030405060708');
                """;
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var db = new Database(path);
        db.Initialize(); // миграция добавит phash_ver DEFAULT 1
        var files = new FileRepository(db);

        // Строка v1 с уже посчитанным phash попадает в пересчёт (иначе смешение версий в группировке).
        Assert.Equal(1, files.CountNeedingPerceptual(FileCategory.Image));
        var page = files.GetNeedingPerceptualPage(FileCategory.Image, 0, 10);
        var rec = Assert.Single(page);
        Assert.Equal(10, rec.Id);
        Assert.Equal(1, rec.PHashVer);

        // После пересчёта текущей версией — выбывает из выборки.
        rec.PHash = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
        rec.PHashVer = ImageMatchService.CurrentPHashVersion;
        files.UpdateComputedBatch(new[] { rec });
        Assert.Equal(0, files.CountNeedingPerceptual(FileCategory.Image));
    }

    // === (д) перенос по full_hash: только pHash текущей версии, получателю ставится та же версия ===
    [Fact]
    public void Propagate_transfers_only_current_version_phash()
    {
        var db = new Database(Path.Combine(_dir, "prop.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        long vid = volumes.Upsert(new VolumeRecord { Serial = "S13-PROP" });

        FileRecord Img(string rel, byte[] fullHash, byte[]? phash, int ver) => new()
        {
            VolumeId = vid, RelPath = rel, Name = rel, Ext = ".jpg", Size = 100,
            Mtime = DateTimeOffset.Now, Category = FileCategory.Image,
            FullHash = fullHash, PHash = phash, PHashVer = ver
        };

        var hashA = new byte[] { 1, 1, 1, 1 };
        var hashB = new byte[] { 2, 2, 2, 2 };
        var srcNew = Img("new_src.jpg", hashA, new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 }, ImageMatchService.CurrentPHashVersion);
        var dstNew = Img("new_dst.jpg", hashA, null, 1);
        var srcOld = Img("old_src.jpg", hashB, new byte[] { 5, 5, 5, 5, 5, 5, 5, 5 }, 1); // старый алгоритм
        var dstOld = Img("old_dst.jpg", hashB, null, 1);
        files.UpsertMetaBatch(new[] { srcNew, dstNew, srcOld, dstOld });

        files.PropagatePerceptualByFullHash(FileCategory.Image);

        // С источника текущей версии — перенеслись и pHash, и версия.
        var gotNew = files.GetById(dstNew.Id)!;
        Assert.Equal(srcNew.PHash, gotNew.PHash);
        Assert.Equal(ImageMatchService.CurrentPHashVersion, gotNew.PHashVer);

        // Со старого (v1) источника перенос запрещён: получатель ждёт честного пересчёта.
        var gotOld = files.GetById(dstOld.Id)!;
        Assert.Null(gotOld.PHash);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
