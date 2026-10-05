using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

/// <summary>
/// Чтение и хэширование содержимого. Используем xxHash3 — очень быстрый
/// некриптографический хэш для воронки фильтрации. Финальная уверенность даётся
/// побайтовой сверкой. Чтение идёт через RandomAccess поверх SafeFileHandle + ArrayPool.
/// </summary>
public static class ContentHasher
{
    private const int FullBufferBytes = 1 << 20; // 1 МБ

    private static SafeFileHandle Open(string path, bool sequential)
    {
        var opts = FileOptions.Asynchronous |
                   (sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);
        return File.OpenHandle(Native.LongPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, opts);
    }

    /// <summary>
    /// Хэш «голова + хвост». Маленькие файлы (≤ 2×chunk) хэшируем целиком.
    /// При verifyStableSize=true файл пропускается, если размер изменился после metadata-stage.
    /// </summary>
    public static async Task<(ulong hash, long bytesRead)> HeadTailHashAsync(
        string path, long size, int chunk, CancellationToken ct, bool verifyStableSize = true)
    {
        using var handle = Open(path, sequential: false);
        size = ValidateInitialLength(handle, path, size, verifyStableSize);

        var hasher = new XxHash3();
        long total = 0;

        if (size <= (long)chunk * 2)
        {
            int cap = (int)Math.Max(1, Math.Min(size, FullBufferBytes));
            byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
            try
            {
                long off = 0;
                while (off < size)
                {
                    int want = (int)Math.Min(buf.Length, size - off);
                    int n = await RandomAccess.ReadAsync(handle, buf.AsMemory(0, want), off, ct)
                        .ConfigureAwait(false);
                    if (n <= 0) break;
                    hasher.Append(buf.AsSpan(0, n));
                    off += n; total += n;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        else
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(chunk);
            try
            {
                int head = await ReadAtAsync(handle, buf, 0, chunk, ct).ConfigureAwait(false);
                hasher.Append(buf.AsSpan(0, head));
                total += head;

                int tail = await ReadAtAsync(handle, buf, size - chunk, chunk, ct).ConfigureAwait(false);
                hasher.Append(buf.AsSpan(0, tail));
                total += tail;
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        ValidateFinalLength(handle, path, size, verifyStableSize);
        return (hasher.GetCurrentHashAsUInt64(), total);
    }

    /// <summary>Полный потоковый хэш всего файла.</summary>
    public static Task<(ulong hash, long bytesRead)> FullHashAsync(string path, CancellationToken ct)
        => FullHashAsync(path, expectedSize: null, ct, verifyStableSize: false);

    /// <summary>Полный потоковый хэш с проверкой стабильности размера.</summary>
    public static async Task<(ulong hash, long bytesRead)> FullHashAsync(
        string path, long expectedSize, CancellationToken ct, bool verifyStableSize = true)
        => await FullHashAsync(path, (long?)expectedSize, ct, verifyStableSize).ConfigureAwait(false);

    private static async Task<(ulong hash, long bytesRead)> FullHashAsync(
        string path, long? expectedSize, CancellationToken ct, bool verifyStableSize)
    {
        using var handle = Open(path, sequential: true);
        long size = expectedSize is long expected
            ? ValidateInitialLength(handle, path, expected, verifyStableSize)
            : RandomAccess.GetLength(handle);

        var hasher = new XxHash3();
        byte[] buf = ArrayPool<byte>.Shared.Rent(FullBufferBytes);
        long off = 0, total = 0;
        try
        {
            while (off < size)
            {
                int want = (int)Math.Min(FullBufferBytes, size - off);
                int n = await RandomAccess.ReadAsync(handle, buf.AsMemory(0, want), off, ct)
                    .ConfigureAwait(false);
                if (n <= 0) break;
                hasher.Append(buf.AsSpan(0, n));
                off += n; total += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }

        ValidateFinalLength(handle, path, size, verifyStableSize);
        return (hasher.GetCurrentHashAsUInt64(), total);
    }

    /// <summary>Побайтовая сверка двух файлов — финальная гарантия перед любым удалением.</summary>
    public static async Task<bool> FilesEqualAsync(string a, string b, CancellationToken ct)
    {
        using var ha = Open(a, sequential: true);
        using var hb = Open(b, sequential: true);

        long la = RandomAccess.GetLength(ha);
        long lb = RandomAccess.GetLength(hb);
        if (la != lb) return false;

        byte[] ba = ArrayPool<byte>.Shared.Rent(FullBufferBytes);
        byte[] bb = ArrayPool<byte>.Shared.Rent(FullBufferBytes);
        long off = 0;
        try
        {
            while (off < la)
            {
                int want = (int)Math.Min(FullBufferBytes, la - off);
                int na = await ReadAtAsync(ha, ba, off, want, ct).ConfigureAwait(false);
                int nb = await ReadAtAsync(hb, bb, off, want, ct).ConfigureAwait(false);
                if (na != nb) return false;
                if (na == 0) break;
                if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
                off += na;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ba);
            ArrayPool<byte>.Shared.Return(bb);
        }

        // Если файл менялся во время сравнения, не считаем его доказанным дублем.
        return RandomAccess.GetLength(ha) == la && RandomAccess.GetLength(hb) == lb;
    }

    /// <summary>Дочитывает до len байт по смещению (одно системное чтение может вернуть меньше).</summary>
    private static async Task<int> ReadAtAsync(SafeFileHandle h, byte[] buf, long offset, int len, CancellationToken ct)
    {
        int got = 0;
        while (got < len)
        {
            int n = await RandomAccess.ReadAsync(h, buf.AsMemory(got, len - got), offset + got, ct)
                .ConfigureAwait(false);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    private static long ValidateInitialLength(SafeFileHandle handle, string path, long expectedSize, bool verifyStableSize)
    {
        // Проверка выключена — доверяем переданному размеру, без лишнего syscall GetLength на каждый кандидат.
        if (!verifyStableSize) return expectedSize;
        long actual = RandomAccess.GetLength(handle);
        if (actual != expectedSize)
            throw new FileChangedDuringScanException(path, expectedSize, actual);
        return actual;
    }

    private static void ValidateFinalLength(SafeFileHandle handle, string path, long expectedSize, bool verifyStableSize)
    {
        if (!verifyStableSize) return;
        long actual = RandomAccess.GetLength(handle);
        if (actual != expectedSize)
            throw new FileChangedDuringScanException(path, expectedSize, actual);
    }
}
