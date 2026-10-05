using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Hashing;

public interface IHashService
{
    /// <summary>Частичный хэш: голова + хвост (~64 КБ с каждого края); для файлов больше
    /// <see cref="HashService.MiddleBlockThreshold"/> — плюс средний блок (v2, этап 1.6:
    /// видео/ISO с одинаковой «шапкой» перестают уходить в полный хэш гигабайт).</summary>
    /// <remarks>Проверяет стабильность размера файла до и после чтения: если файл изменился
    /// между сбором метаданных и чтением, бросает <see cref="FileChangedDuringScanException"/>,
    /// чтобы старый размер не смешивался с новым содержимым.
    /// preferWholeRead=true (носитель со штрафом позиционирования): файлы до ~1 МБ читаются
    /// ОДНИМ последовательным чтением вместо двух сиков «голова+хвост» — хэш байтово тот же
    /// (этап 1.4: −1 сик на каждый файл-кандидат, а это типичный размер фото-миниатюр/доков).</remarks>
    byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default, bool preferWholeRead = false);

    /// <summary>Полный хэш всего содержимого файла (читается целиком).</summary>
    byte[] ComputeFullHash(string fullPath, CancellationToken ct = default);

    /// <summary>Полный хэш с проверкой стабильности размера: если файл вырос/сжался между
    /// metadata-этапом и чтением, бросает <see cref="FileChangedDuringScanException"/> —
    /// такой файл нельзя считать доказанным дублем в этом проходе.</summary>
    byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default);

    /// <summary>Финальная побайтовая сверка двух файлов (защита от коллизий хэша, FR-11).</summary>
    /// <param name="blockBytes">Размер блока чтения; 0 — по умолчанию (1 МБ). Когда оба файла на
    /// одном носителе со штрафом позиционирования, крупный блок (16 МБ) сокращает чередование
    /// головки A/B в ~16 раз (этап 1.3: пара 2 ГБ на HDD — десятки секунд позиционирования → секунды).</param>
    bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default, int blockBytes = 0);
}

/// <summary>
/// Файл изменился между сбором метаданных и чтением содержимого. Безопаснее пропустить его в
/// этом проходе и взять на следующем инкрементальном скане, чем смешать старый размер с новым хэшем.
/// </summary>
public sealed class FileChangedDuringScanException : IOException
{
    public string FilePath { get; }
    public long ExpectedSize { get; }
    public long ActualSize { get; }

    public FileChangedDuringScanException(string path, long expectedSize, long actualSize)
        : base($"Файл изменился во время сверки: ожидался размер {expectedSize}, фактический {actualSize}: {path}")
    {
        FilePath = path;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }
}

/// <summary>
/// Хэширование на XxHash128 (System.IO.Hashing). Криптостойкость не требуется —
/// итоговую идентичность подтверждает побайтовая сверка.
/// </summary>
public sealed class HashService : IHashService
{
    private const int EdgeBytes = 64 * 1024;   // 64 КБ с каждого края для частичного хэша
    private const int StreamBuffer = 1 << 20;  // 1 МБ буфер чтения

    /// <summary>Файлы крупнее этого порога получают в частичный хэш третий (средний) блок (v2).</summary>
    public const long MiddleBlockThreshold = 128L * 1024 * 1024;

    /// <summary>Текущая версия алгоритма частичного хэша (пишется в files.partial_ver).</summary>
    public const int CurrentPartialVersion = 2;

    /// <summary>До ~1 МБ выгоднее прочитать файл целиком одним чтением, чем двумя сиками (HDD).</summary>
    private const long SingleReadCap = 1L << 20;

    /// <summary>Сколько байтов успешный partial-проход реально читает с носителя.</summary>
    public static long EstimatePartialBytes(long size, bool preferWholeRead)
    {
        if (size <= 0) return 0;
        if (size <= EdgeBytes * 2L) return size;
        if (preferWholeRead && size <= SingleReadCap) return size;
        return EdgeBytes * 2L + (size > MiddleBlockThreshold ? EdgeBytes : 0);
    }

    public byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default, bool preferWholeRead = false)
    {
        // P1.3: голову/хвост читаем по СМЕЩЕНИЮ через RandomAccess (без перемотки позиции потока) —
        // меньше seek-нагрузка на HDD/USB. Порядок добавления (голова + размер + [средний] + хвост)
        // фиксирован; для файлов ≤ MiddleBlockThreshold хэш байтово совпадает с прежней версией.
        ThrowIfCloudPlaceholder(fullPath);
        using var handle = OpenHandle(fullPath);
        long actual = RandomAccess.GetLength(handle);
        if (actual != size) throw new FileChangedDuringScanException(fullPath, size, actual);
        var hasher = new XxHash128();

        if (size <= EdgeBytes * 2L)
        {
            // Маленький файл — хэшируем целиком, это и есть «частичный».
            AppendWhole(handle, size, hasher, ct);
            VerifyStableSize(handle, fullPath, size);
            return hasher.GetCurrentHash();
        }

        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, size);

        if (preferWholeRead && size <= SingleReadCap)
        {
            // 1.4: носитель со штрафом позиционирования — файл до 1 МБ читаем ОДНИМ последовательным
            // чтением и берём голову/хвост из буфера. Байты в хэшере те же → хэш идентичен, миграция
            // БД не нужна; экономится один сик на каждый файл-кандидат (типичный размер фото/доков).
            var whole = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                int read = ReadAtOffset(handle, whole.AsSpan(0, (int)size), 0, ct);
                int head = Math.Min(EdgeBytes, read);
                hasher.Append(whole.AsSpan(0, head));
                hasher.Append(sizeBytes);
                int tailStart = (int)Math.Max(0, size - EdgeBytes);
                hasher.Append(whole.AsSpan(tailStart, Math.Max(0, read - tailStart)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(whole);
            }
            VerifyStableSize(handle, fullPath, size);
            return hasher.GetCurrentHash();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes);
        try
        {
            int headRead = ReadAtOffset(handle, buffer.AsSpan(0, EdgeBytes), 0, ct);
            hasher.Append(buffer.AsSpan(0, headRead));

            // Включаем размер в хэш, чтобы файлы разной длины не сталкивались по краям.
            hasher.Append(sizeBytes);

            // 1.6 (v2): для крупных файлов — средний блок. Видео/образы с одинаковыми первыми и
            // последними мегабайтами (шапка контейнера + финализация) различаются серединой —
            // без неё они уходили в полный хэш гигабайт ради отсева.
            if (size > MiddleBlockThreshold)
            {
                int midRead = ReadAtOffset(handle, buffer.AsSpan(0, EdgeBytes), size / 2, ct);
                hasher.Append(buffer.AsSpan(0, midRead));
            }

            // Хвост — читаем по абсолютному смещению size - EdgeBytes.
            int tailRead = ReadAtOffset(handle, buffer.AsSpan(0, EdgeBytes), size - EdgeBytes, ct);
            hasher.Append(buffer.AsSpan(0, tailRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        // Размер менялся во время чтения — голова/хвост могли быть прочитаны из разных версий.
        VerifyStableSize(handle, fullPath, size);
        return hasher.GetCurrentHash();
    }

    public byte[] ComputeFullHash(string fullPath, CancellationToken ct = default)
    {
        ThrowIfCloudPlaceholder(fullPath);
        using var fs = OpenRead(fullPath);
        var hasher = new XxHash128();
        AppendStream(fs, hasher, ct);
        return hasher.GetCurrentHash();
    }

    public byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default)
    {
        ThrowIfCloudPlaceholder(fullPath);
        using var fs = OpenRead(fullPath);
        VerifyStableSize(fs, fullPath, expectedSize);
        var hasher = new XxHash128();
        AppendStream(fs, hasher, ct);
        // Файл рос во время полного хэша (например, активно пишущийся лог) — не считаем доказанным.
        VerifyStableSize(fs, fullPath, expectedSize);
        return hasher.GetCurrentHash();
    }

    /// <summary>
    /// Сверяет фактическую длину открытого файла с ожидаемой. Длину берём свежим syscall через
    /// <see cref="RandomAccess.GetLength"/> по дескриптору потока (а не кэшируемый FileStream.Length),
    /// иначе внешняя дозапись не была бы видна.
    /// </summary>
    private static void VerifyStableSize(FileStream fs, string path, long expectedSize) =>
        VerifyStableSize(fs.SafeFileHandle, path, expectedSize);

    private static void VerifyStableSize(SafeFileHandle handle, string path, long expectedSize)
    {
        long actual = RandomAccess.GetLength(handle);
        if (actual != expectedSize)
            throw new FileChangedDuringScanException(path, expectedSize, actual);
    }

    /// <summary>
    /// Облачные плейсхолдеры (OneDrive «Файлы по запросу») не читаем: открытие содержимого
    /// заставит провайдера скачать файл из облака. Бросаем
    /// <see cref="FileChangedDuringScanException"/>-совместимый отказ через IOException —
    /// конвейер обрабатывает его как «файл пропущен в этом проходе».
    /// </summary>
    private static void ThrowIfCloudPlaceholder(string path)
    {
        if (CloudFiles.IsPlaceholderPath(path))
            throw new IOException($"Облачный файл-плейсхолдер — содержимое не читаем, чтобы не скачивать из облака: {path}");
    }

    /// <summary>Дескриптор только для чтения (offset-доступ через RandomAccess), без перемотки позиции.</summary>
    private static SafeFileHandle OpenHandle(string path) =>
        File.OpenHandle(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);

    /// <summary>Прочитать до buffer.Length байт по абсолютному смещению; вернуть фактически прочитанное.</summary>
    private static int ReadAtOffset(SafeFileHandle handle, Span<byte> buffer, long offset, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(handle, buffer.Slice(total), offset + total);
            if (n == 0) break; // EOF
            total += n;
        }
        return total;
    }

    /// <summary>Хэшировать весь файл по дескриптору (для маленьких файлов в частичном хэше).</summary>
    private static void AppendWhole(SafeFileHandle handle, long size, XxHash128 hasher, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        try
        {
            long offset = 0;
            while (offset < size)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, size - offset);
                int n = RandomAccess.Read(handle, buffer.AsSpan(0, want), offset);
                if (n == 0) break;
                hasher.Append(buffer.AsSpan(0, n));
                offset += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default, int blockBytes = 0)
    {
        // Облачный плейсхолдер сверить нельзя, не скачав его содержимое — консервативно «не равны»
        // (файл останется несгруппированным/несклеенным; данные не рискуют, трафика нет).
        if (CloudFiles.IsPlaceholderPath(pathA) || CloudFiles.IsPlaceholderPath(pathB)) return false;

        int block = blockBytes > 0 ? blockBytes : StreamBuffer;

        using var a = OpenRead(pathA);
        using var b = OpenRead(pathB);
        if (a.Length != b.Length) return false;

        var bufA = ArrayPool<byte>.Shared.Rent(block);
        var bufB = ArrayPool<byte>.Shared.Rent(block);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int nA = ReadBlock(a, bufA, block);
                int nB = ReadBlock(b, bufB, block);
                if (nA != nB) return false;
                if (nA == 0) return true;
                if (!bufA.AsSpan(0, nA).SequenceEqual(bufB.AsSpan(0, nB))) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufA);
            ArrayPool<byte>.Shared.Return(bufB);
        }
    }

    private static FileStream OpenRead(string path) =>
        new(LongPath.Fix(path), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Разрешаем доступ к файлам, открытым другими процессами на чтение/запись.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = StreamBuffer
        });

    private static void AppendStream(FileStream fs, XxHash128 hasher, CancellationToken ct)
    {
        // 1.8: двойная буферизация — пока CPU хэширует текущий блок, диск уже читает следующий
        // (перекрытие I/O и вычислений даёт +10–25% на полном хэше; сам хэш не меняется).
        var handle = fs.SafeFileHandle;
        var bufA = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        var bufB = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        Task<int>? pending = null;
        try
        {
            long offset = 0;
            var cur = bufA;
            var next = bufB;
            pending = RandomAccess.ReadAsync(handle, cur.AsMemory(0, StreamBuffer), offset, ct).AsTask();
            while (true)
            {
                int n = pending.GetAwaiter().GetResult();
                pending = null;
                if (n == 0) break;
                offset += n;
                pending = RandomAccess.ReadAsync(handle, next.AsMemory(0, StreamBuffer), offset, ct).AsTask();
                hasher.Append(cur.AsSpan(0, n));
                (cur, next) = (next, cur);
            }
        }
        finally
        {
            // Буферы нельзя вернуть в пул, пока последнее чтение в полёте (оно пишет в next):
            // дожидаемся его даже на исключении/отмене.
            if (pending is not null) { try { pending.Wait(CancellationToken.None); } catch { } }
            ArrayPool<byte>.Shared.Return(bufA);
            ArrayPool<byte>.Shared.Return(bufB);
        }
    }

    private static int ReadBlock(FileStream fs, byte[] buffer, int block)
    {
        int read = 0;
        while (read < block)
        {
            int n = fs.Read(buffer, read, block - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
