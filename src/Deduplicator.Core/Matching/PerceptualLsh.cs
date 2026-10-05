namespace Deduplicator.Core.Matching;

/// <summary>
/// LSH для 64-битных перцептивных хэшей (bit-sampling по полосам).
/// 8 полос по 8 бит: случайные пары почти не сталкиваются (≈0.03 ожидаемых полос),
/// а пары с расстоянием Хэмминга ≤ 7 гарантированно попадают в общую корзину
/// (что соответствует схожести ≥ ~89%).
/// </summary>
public static class PerceptualLsh
{
    public const int Bands = 8;
    public const int BandBits = 8;
    public const int TotalBits = 64;

    public static ulong ToHash(byte[]? phash) =>
        phash is { Length: >= 8 } ? BitConverter.ToUInt64(phash, 0) : 0UL;

    public static byte[] ToBytes(ulong hash) => BitConverter.GetBytes(hash);

    /// <summary>Полосовые ключи (band, key) для индексации хэша.</summary>
    public static IEnumerable<(int band, long key)> BandKeys(ulong hash)
    {
        for (int b = 0; b < Bands; b++)
            yield return (b, BandKey(hash, b));
    }

    /// <summary>Ключ одной полосы (8 бит) — для дробления крупных корзин LSH.</summary>
    public static long BandKey(ulong hash, int band) =>
        (long)((hash >> (band * BandBits)) & 0xFF);

    public static int Hamming(ulong a, ulong b) =>
        System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>Схожесть 0..1 = 1 - расстояние/64.</summary>
    public static double Similarity(ulong a, ulong b) =>
        1.0 - Hamming(a, b) / (double)TotalBits;
}
