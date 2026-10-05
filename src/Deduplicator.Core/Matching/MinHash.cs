namespace Deduplicator.Core.Matching;

/// <summary>
/// MinHash + LSH по множествам субсигнатур аудио-отпечатка. Позволяет находить
/// «ту же запись в разных форматах»: пересохранение в другой битрейт/контейнер
/// сохраняет высокую долю общих субсигнатур (Жаккар).
/// </summary>
public sealed class MinHasher
{
    public const int K = 40;        // длина сигнатуры
    public const int Bands = 10;    // полос
    public const int Rows = 4;      // строк в полосе (Bands*Rows == K)
    private const uint Prime = 4294967291u; // наибольшее простое < 2^32

    private readonly uint[] _a = new uint[K];
    private readonly uint[] _b = new uint[K];

    public MinHasher(int seed = 1469598103)
    {
        // Детерминированный LCG — стабильные коэффициенты между запусками.
        ulong state = (ulong)seed | 1UL;
        for (int i = 0; i < K; i++)
        {
            _a[i] = NextOdd(ref state);
            _b[i] = Next(ref state);
        }
    }

    public uint[] Signature(IReadOnlyCollection<uint> set)
    {
        var sig = new uint[K];
        for (int k = 0; k < K; k++) sig[k] = uint.MaxValue;
        if (set.Count == 0) return sig;

        foreach (var x in set)
        {
            for (int k = 0; k < K; k++)
            {
                uint h = (uint)(((ulong)_a[k] * x + _b[k]) % Prime);
                if (h < sig[k]) sig[k] = h;
            }
        }
        return sig;
    }

    /// <summary>Ключи полос (band, key) для индексации сигнатуры.</summary>
    public static IEnumerable<(int band, long key)> BandKeys(uint[] signature)
    {
        for (int band = 0; band < Bands; band++)
            yield return (band, BandKey(signature, band));
    }

    /// <summary>Ключ одной полосы сигнатуры — для дробления крупных корзин LSH.</summary>
    public static long BandKey(uint[] signature, int band)
    {
        ulong acc = 1469598103934665603UL; // FNV offset
        for (int r = 0; r < Rows; r++)
        {
            acc ^= signature[band * Rows + r];
            acc *= 1099511628211UL;
        }
        return (long)acc;
    }

    public static double Jaccard(HashSet<uint> a, HashSet<uint> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;
        int inter = 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var x in small) if (large.Contains(x)) inter++;
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0.0 : inter / (double)union;
    }

    private static uint Next(ref ulong state)
    {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        return (uint)(state >> 32);
    }

    private static uint NextOdd(ref ulong state)
    {
        var v = Next(ref state);
        return v | 1u;
    }
}
