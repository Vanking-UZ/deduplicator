using System.IO.Hashing;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using Deduplicator.Core.Util;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Imaging;

/// <summary>
/// Перцептивные признаки изображения, считаемые за ОДИН декод (3.7): pHash и резкость.
/// Оба null — файл не изображение, битый, облачный плейсхолдер или иконка меньше префильтра.
/// </summary>
public readonly record struct ImageSignatures(byte[]? PHash, double? Sharpness);

public interface IImageMatchService
{
    /// <summary>Перцептивный хэш изображения (64-битный pHash). null — если файл не читается.</summary>
    byte[]? ComputePHash(string fullPath, CancellationToken ct = default);

    /// <summary>
    /// pHash по уже прочитанному содержимому файла (конвейер чтение→декод, этап 2.4).
    /// Байт в байт тот же результат, что по пути. Облачный guard — на совести читателя
    /// (проверка атрибутов по пути ДО чтения содержимого).
    /// </summary>
    byte[]? ComputePHash(ReadOnlyMemory<byte> content, CancellationToken ct = default);

    /// <summary>
    /// 3.7: pHash + резкость за ОДИН декод (второй проход по диску/декодеру недопустим —
    /// резкость считается на той же ужатой копии, что и pHash). PHash байт в байт совпадает
    /// с <see cref="ComputePHash(string,CancellationToken)"/> — тот же core.
    /// </summary>
    ImageSignatures ComputeSignatures(string fullPath, CancellationToken ct = default);

    /// <summary>Признаки по содержимому в памяти (конвейер 2.4) — см. <see cref="ComputeSignatures(string,CancellationToken)"/>.</summary>
    ImageSignatures ComputeSignatures(ReadOnlyMemory<byte> content, CancellationToken ct = default);

    /// <summary>Хэш декодированных пикселей (для одинаковых картинок с разными метаданными).</summary>
    byte[]? ComputePixelHash(string fullPath, CancellationToken ct = default);

    /// <summary>Pixel-hash по содержимому в памяти (см. <see cref="ComputePHash(ReadOnlyMemory{byte},CancellationToken)"/>).</summary>
    byte[]? ComputePixelHash(ReadOnlyMemory<byte> content, CancellationToken ct = default);

    /// <summary>Сгенерировать (если нужно) миниатюру и вернуть путь к кэшированному файлу.</summary>
    string? EnsureThumbnail(string fullPath, string cacheKey, int maxSize = 256);

    /// <summary>
    /// Размеры изображения в пикселях БЕЗ декодирования (Image.Identify читает только заголовок).
    /// Нужно просмотрщику сравнения: показ идёт по ужатой копии, а честное разрешение — отсюда.
    /// null — файл не изображение, битый или недоступен (в т.ч. облачный плейсхолдер, B4).
    /// </summary>
    (int Width, int Height)? GetDimensions(string fullPath);
}

/// <summary>pHash, pixel-hash и миниатюры на ImageSharp (полностью управляемый код).</summary>
public sealed class ImageMatchService : IImageMatchService
{
    /// <summary>
    /// Текущая версия алгоритма pHash (пишется в files.phash_ver). v2 (этап 2.2): декод сразу
    /// в TargetSize 256×256 (scaled-IDCT для JPEG) вместо полноразмерного — значения сдвигаются
    /// на единицы бит относительно v1, поэтому старые строки пересчитываются (смешение версий
    /// в одной группировке у порога Хэмминга давало бы кашу).
    /// </summary>
    public const int CurrentPHashVersion = 2;

    /// <summary>Меньшая сторона декода для pHash: с запасом больше внутренних 64×64 PerceptualHash.</summary>
    private const int PHashDecodeSize = 256;

    /// <summary>Иконки меньше этого по меньшей стороне перцептивно не сравниваем (префильтр).</summary>
    private const int MinPHashSide = 64;

    private static readonly IImageHash HashAlgo = new PerceptualHash();
    private readonly string _thumbDir;
    private readonly bool _rotationInvariant;

    public ImageMatchService(string thumbnailCacheDir, bool rotationInvariant = true)
    {
        _thumbDir = thumbnailCacheDir;
        _rotationInvariant = rotationInvariant;
        Directory.CreateDirectory(_thumbDir);
    }

    // 3.7: старые перегрузки ComputePHash сохранены для сторонних вызовов и делегируют в
    // ComputeSignatures (минимально-инвазивный вариант: сигнатуры существующих методов не меняются).
    public byte[]? ComputePHash(string fullPath, CancellationToken ct = default) =>
        ComputeSignatures(fullPath, ct).PHash;

    public byte[]? ComputePHash(ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
        ComputeSignatures(content, ct).PHash;

    public ImageSignatures ComputeSignatures(string fullPath, CancellationToken ct = default)
    {
        if (CloudFiles.IsPlaceholderPath(fullPath)) return default; // не скачиваем из облака (B4)
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fs = OpenRead(fullPath);
            return ComputeSignaturesCore(fs);
        }
        catch (OperationCanceledException) { throw; }
        catch { return default; } // битый/нечитаемый файл — пропускаем (NFR-1)
    }

    /// <summary>
    /// Признаки из уже прочитанного содержимого (конвейер чтение→декод, этап 2.4). Облачный guard
    /// здесь не нужен: содержимое уже в памяти, атрибуты проверяет продюсер ДО чтения.
    /// Тот же ComputeSignaturesCore, что и по пути, — результат байт в байт совпадает.
    /// </summary>
    public ImageSignatures ComputeSignatures(ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var ms = AsStream(content);
            return ComputeSignaturesCore(ms);
        }
        catch (OperationCanceledException) { throw; }
        catch { return default; } // битое содержимое — пропускаем (NFR-1)
    }

    private ImageSignatures ComputeSignaturesCore(Stream stream)
    {
        // Префильтр по метаданным (дёшево, пиксели не декодируются): битый файл здесь же
        // бросит исключение → null, а иконки меньше 64×64 по меньшей стороне перцептивно
        // не сравниваем — их pHash почти не различителен и только шумит в группировке.
        var info = Image.Identify(stream);
        if (Math.Min(info.Width, info.Height) < MinPHashSide) return default;
        stream.Position = 0;

        // Этап 2.2: декодируем сразу в маленький размер (scaled-IDCT для JPEG — в разы
        // быстрее и без полноразмерного буфера; 24 МП ради 64×64 целиком не разжимаем).
        // ВАЖНО: TargetSize задаём только когда исходник КРУПНЕЕ цели — ImageSharp иначе
        // РАСТЯГИВАЕТ мелкие картинки до цели, и pHash уезжает на десяток бит (проверено:
        // 160×160 → 256×256 давало ~14 бит сдвига и роняло похожесть ниже порога).
        var opts = Math.Max(info.Width, info.Height) > PHashDecodeSize
            ? new DecoderOptions { TargetSize = new Size(PHashDecodeSize, PHashDecodeSize) }
            : new DecoderOptions();
        using var img = Image.Load<Rgba32>(opts, stream);

        // 3.7: резкость — на ТОЙ ЖЕ ужатой копии, что и pHash (отдельного декода нет; 256 px
        // достаточно, потому что резкость сравнивается только МЕЖДУ кадрами одной серии, где
        // все члены прошли тот же даунскейл). Резкость — не хэш: версия pHash её не касается.
        double sharpness = ComputeSharpness(img);

        if (!_rotationInvariant)
            return new ImageSignatures(BitConverter.GetBytes(HashAlgo.Hash(img)), sharpness);

        // Канонический хэш = минимум по ориентациям (повороты + зеркало).
        // Тогда повёрнутые/отражённые копии дают одинаковое значение и группируются.
        // Все 8 ориентаций считаются на уже УЖАТОЙ копии — клоны копеечные.
        // Полная диэдральная группа: 4 поворота × 2 отражения = 8 ориентаций.
        ulong best = HashAlgo.Hash(img);
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate90, FlipMode.None));
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate180, FlipMode.None));
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate270, FlipMode.None));
        best = Math.Min(best, HashOriented(img, RotateMode.None, FlipMode.Horizontal));
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate90, FlipMode.Horizontal));
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate180, FlipMode.Horizontal));
        best = Math.Min(best, HashOriented(img, RotateMode.Rotate270, FlipMode.Horizontal));
        return new ImageSignatures(BitConverter.GetBytes(best), sharpness);
    }

    /// <summary>
    /// 3.7: резкость кадра — дисперсия отклика Лапласиана по яркости (стандартная метрика
    /// «variance of Laplacian»). Яркость: 0.299R + 0.587G + 0.114B; свёртка 3×3 «крестом»
    /// (центр ×4, соседи −1); чем больше дисперсия, тем резче кадр. Значение зависит от размера
    /// декода, поэтому сравнивать резкость имеет смысл только между кадрами, декодированными
    /// одинаково (у нас — та же 256px копия для всех). Чистая функция — тестируется синтетикой.
    /// </summary>
    public static double ComputeSharpness(Image<Rgba32> img)
    {
        int w = img.Width, h = img.Height;
        if (w < 3 || h < 3) return 0; // для свёртки нужны внутренние пиксели

        var lum = new float[w * h];
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                int off = y * w;
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    lum[off + x] = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
                }
            }
        });

        // Дисперсия отклика по внутренним пикселям (границы без полного креста не считаем).
        double sum = 0, sumSq = 0;
        long n = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int off = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                double v = 4f * lum[off + x]
                           - lum[off + x - 1] - lum[off + x + 1]
                           - lum[off + x - w] - lum[off + x + w];
                sum += v;
                sumSq += v * v;
                n++;
            }
        }
        if (n == 0) return 0;
        double mean = sum / n;
        return Math.Max(0, sumSq / n - mean * mean); // клэмп от отрицательного нуля float-погрешности
    }

    private static ulong HashOriented(Image<Rgba32> img, RotateMode rotate, FlipMode flip)
    {
        using var clone = img.Clone(c => c.RotateFlip(rotate, flip));
        return HashAlgo.Hash(clone);
    }

    public byte[]? ComputePixelHash(string fullPath, CancellationToken ct = default)
    {
        if (CloudFiles.IsPlaceholderPath(fullPath)) return null; // не скачиваем из облака (B4)
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fs = OpenRead(fullPath);
            return ComputePixelHashCore(fs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Pixel-hash из памяти (конвейер 2.4) — тот же core, что и по пути, байт в байт.</summary>
    public byte[]? ComputePixelHash(ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var ms = AsStream(content);
            return ComputePixelHashCore(ms, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static byte[] ComputePixelHashCore(Stream stream, CancellationToken ct)
    {
        // Pixel-hash — «одинаковые пиксели, разные метаданные» (FR-14): декодируем
        // ПОЛНОРАЗМЕРНО (ужатая копия дала бы другой хэш), но хэшируем потоково по строкам.
        // Прежний byte[W*H*4] на >2 ГБ панорамах переполнял int и молча возвращал null;
        // результат байтово совпадает с прежним (те же RGBA-строки сверху вниз).
        using var img = Image.Load<Rgba32>(stream);
        var hasher = new XxHash128();
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(acc.GetRowSpan(y)));
            }
        });
        return hasher.GetCurrentHash();
    }

    public string? EnsureThumbnail(string fullPath, string cacheKey, int maxSize = 256)
    {
        var safeKey = MakeSafe(cacheKey);
        var thumbPath = Path.Combine(_thumbDir, $"{safeKey}_{maxSize}.jpg");
        if (File.Exists(thumbPath)) return thumbPath;
        if (CloudFiles.IsPlaceholderPath(fullPath)) return null; // не скачиваем из облака (B4)

        // Пишем во временный файл и атомарно переименовываем: сбой при кодировании не оставит
        // «обрезанный» .jpg, который при следующем вызове вернулся бы как валидный.
        var tmp = thumbPath + "." + Guid.NewGuid().ToString("N") + SafeIo.TempSuffix;
        try
        {
            using (var fs = OpenRead(fullPath))
            using (var img = LoadForThumbnail(fs, maxSize))
            {
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxSize, maxSize),
                    Mode = ResizeMode.Max
                }));
                using var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                img.SaveAsJpeg(outFs);
            }
            File.Move(tmp, thumbPath, overwrite: true); // атомарная публикация в кэш
            return thumbPath;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return null;
        }
    }

    public (int Width, int Height)? GetDimensions(string fullPath)
    {
        if (CloudFiles.IsPlaceholderPath(fullPath)) return null; // не скачиваем из облака (B4)
        try
        {
            using var fs = OpenRead(fullPath);
            var info = Image.Identify(fs); // только метаданные, пиксели не декодируются
            return (info.Width, info.Height);
        }
        catch { return null; } // не изображение/битый файл — разрешение просто не показываем
    }

    /// <summary>
    /// Источник для миниатюры (этап 1.11). Сначала пробуем ВСТРОЕННУЮ EXIF-миниатюру: Identify
    /// читает только метаданные, пиксели оригинала вообще не декодируются — открытие результатов
    /// с сотнями фото ускоряется на порядок. Если её нет или она слишком мала — полный декод, но
    /// сразу в маленький размер (TargetSize: scaled-IDCT для JPEG, без полноразмерного буфера).
    /// </summary>
    private static Image LoadForThumbnail(FileStream fs, int maxSize)
    {
        try
        {
            var info = Image.Identify(fs);
            if (info.Metadata.ExifProfile is { } exif &&
                exif.TryCreateThumbnail(out Image? exifThumb) && exifThumb is not null)
            {
                // Слишком мелкую EXIF-миниатюру (обычно 160×120) не растягиваем на большой размер.
                if (Math.Max(exifThumb.Width, exifThumb.Height) >= maxSize)
                    return exifThumb;
                exifThumb.Dispose();
            }
        }
        catch { /* нет/битый EXIF — обычный путь */ }

        fs.Position = 0;
        return Image.Load(new DecoderOptions
        {
            TargetSize = new Size(maxSize * 2, maxSize * 2)
        }, fs);
    }

    private static FileStream OpenRead(string path) =>
        new(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    /// <summary>MemoryStream поверх буфера БЕЗ копирования (если память обёрнута над массивом,
    /// как в конвейере 2.4); иначе — единственная копия через ToArray.</summary>
    private static MemoryStream AsStream(ReadOnlyMemory<byte> content) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(content, out var seg) && seg.Array is not null
            ? new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);

    private static string MakeSafe(string key)
    {
        Span<char> buf = stackalloc char[key.Length];
        for (int i = 0; i < key.Length; i++)
            buf[i] = char.IsLetterOrDigit(key[i]) ? key[i] : '_';
        return new string(buf);
    }
}
