using Deduplicator.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Tests;

public class ImageHashTests : IDisposable
{
    private readonly string _dir;

    public ImageHashTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private (string a, string rotated) MakePair()
    {
        var a = Path.Combine(_dir, "a.png");
        var b = Path.Combine(_dir, "b.png");
        using var img = new Image<Rgba32>(200, 160);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 2), (byte)(y * 3), (byte)(x + y), 255);
            }
        });
        img.SaveAsPng(a);
        using (var r = img.Clone(c => c.Rotate(90f))) r.SaveAsPng(b);
        return (a, b);
    }

    [Fact]
    public void RotationInvariant_canonical_hash_equal_for_rotated_image()
    {
        var (a, b) = MakePair();
        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        var ha = svc.ComputePHash(a);
        var hb = svc.ComputePHash(b);
        Assert.NotNull(ha);
        Assert.Equal(ha, hb); // канонический хэш одинаков для повёрнутой копии
    }

    [Fact]
    public void Plain_hash_differs_for_rotated_image()
    {
        var (a, b) = MakePair();
        var svc = new ImageMatchService(_dir, rotationInvariant: false);
        var ha = svc.ComputePHash(a);
        var hb = svc.ComputePHash(b);
        Assert.NotNull(ha);
        Assert.NotEqual(ha, hb); // обычный pHash чувствителен к повороту
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
