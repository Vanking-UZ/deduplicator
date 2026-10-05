using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IOPath = System.IO.Path;

string outDir = args.Length > 0 ? args[0]
    : @"D:\projact\src\Deduplicator.App\Assets";
Directory.CreateDirectory(outDir);

const int N = 256;
var bg = new LinearGradientBrush(
    new PointF(0, 0), new PointF(0, N), GradientRepetitionMode.None,
    new ColorStop(0f, Color.ParseHex("#0A84FF")),
    new ColorStop(1f, Color.ParseHex("#0A5AD0")));

using var img = new Image<Rgba32>(N, N, new Rgba32(0, 0, 0, 0));
img.Mutate(ctx =>
{
    // Плитка-«сквиркл»
    ctx.Fill(bg, Rounded(16, 16, 224, 224, 54));

    // Глиф «дубликат»: задний лист + зазор фоном + передний лист (метафора copy)
    ctx.Fill(Color.White, Rounded(98, 52, 78, 98, 14));        // задний лист (вверх-вправо)
    ctx.Fill(bg, Rounded(74, 84, 88, 110, 17));                // «вырез» фоном для разделения
    ctx.Fill(Color.White, Rounded(78, 88, 78, 98, 14));        // передний лист (вниз-влево)
});

// Базовый PNG (для предпросмотра и WindowIcon)
img.SaveAsPng(IOPath.Combine(outDir, "app-icon-256.png"));

// Многоразмерный ICO
int[] sizes = { 256, 128, 64, 48, 32, 16 };
var entries = new List<(int size, byte[] png)>();
foreach (var s in sizes)
{
    using var clone = img.Clone(c => c.Resize(s, s));
    using var ms = new MemoryStream();
    clone.SaveAsPng(ms);
    entries.Add((s, ms.ToArray()));
}
WriteIco(IOPath.Combine(outDir, "app.ico"), entries);

Console.WriteLine($"Готово: {outDir}\\app.ico ({entries.Count} размеров) + app-icon-256.png");

static IPathCollection Rounded(float x, float y, float w, float h, float r)
{
    var paths = new IPath[]
    {
        new RectangularPolygon(x + r, y, w - 2 * r, h),
        new RectangularPolygon(x, y + r, w, h - 2 * r),
        new EllipsePolygon(x + r, y + r, r),
        new EllipsePolygon(x + w - r, y + r, r),
        new EllipsePolygon(x + r, y + h - r, r),
        new EllipsePolygon(x + w - r, y + h - r, r),
    };
    return new PathCollection(paths);
}

static void WriteIco(string path, List<(int size, byte[] png)> images)
{
    using var fs = System.IO.File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0);                 // reserved
    bw.Write((short)1);                 // type = icon
    bw.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        bw.Write((byte)(size >= 256 ? 0 : size)); // width  (0 => 256)
        bw.Write((byte)(size >= 256 ? 0 : size)); // height (0 => 256)
        bw.Write((byte)0);              // palette
        bw.Write((byte)0);              // reserved
        bw.Write((short)1);             // color planes
        bw.Write((short)32);            // bits per pixel
        bw.Write(png.Length);
        bw.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) bw.Write(png);
}
