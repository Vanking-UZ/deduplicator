using Deduplicator.Core.Hashing;

namespace Deduplicator.Core.Tests;

public class HashServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly HashService _hash = new();

    public HashServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(string name, byte[] data)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public void Identical_content_yields_equal_full_hash()
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data);
        var a = Write("a.bin", data);
        var b = Write("b.bin", (byte[])data.Clone());

        var ha = _hash.ComputeFullHash(a);
        var hb = _hash.ComputeFullHash(b);
        Assert.Equal(ha, hb);
        Assert.True(_hash.FilesAreByteEqual(a, b));
    }

    [Fact]
    public void Different_content_yields_different_hash_and_not_byte_equal()
    {
        var a = Write("a.bin", new byte[] { 1, 2, 3, 4, 5 });
        var b = Write("b.bin", new byte[] { 1, 2, 3, 4, 6 });

        Assert.NotEqual(_hash.ComputeFullHash(a), _hash.ComputeFullHash(b));
        Assert.False(_hash.FilesAreByteEqual(a, b));
    }

    [Fact]
    public void Partial_hash_differs_for_different_large_files()
    {
        var d1 = new byte[300_000];
        var d2 = new byte[300_000];
        new Random(1).NextBytes(d1);
        new Random(2).NextBytes(d2);
        var a = Write("a.bin", d1);
        var b = Write("b.bin", d2);

        Assert.NotEqual(
            _hash.ComputePartialHash(a, d1.Length),
            _hash.ComputePartialHash(b, d2.Length));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
