using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public class MatchingTests
{
    [Fact]
    public void Perceptual_identical_hash_is_full_similarity()
    {
        ulong h = 0xDEADBEEFCAFEBABE;
        Assert.Equal(0, PerceptualLsh.Hamming(h, h));
        Assert.Equal(1.0, PerceptualLsh.Similarity(h, h), 5);
    }

    [Fact]
    public void Perceptual_one_bit_difference_is_high_similarity()
    {
        ulong a = 0;
        ulong b = 1; // отличается на 1 бит из 64
        Assert.Equal(1, PerceptualLsh.Hamming(a, b));
        Assert.Equal(1.0 - 1.0 / 64, PerceptualLsh.Similarity(a, b), 5);
    }

    [Fact]
    public void Perceptual_band_keys_count_matches_bands()
    {
        var keys = PerceptualLsh.BandKeys(0x0102030405060708).ToList();
        Assert.Equal(PerceptualLsh.Bands, keys.Count);
    }

    [Fact]
    public void Close_hashes_share_at_least_one_band()
    {
        ulong a = 0xFFFF_FFFF_FFFF_FF00;
        ulong b = 0xFFFF_FFFF_FFFF_FF07; // отличие в младшем байте (одна полоса)
        var ka = PerceptualLsh.BandKeys(a).ToHashSet();
        var kb = PerceptualLsh.BandKeys(b).ToHashSet();
        Assert.True(ka.Overlaps(kb));
    }

    [Fact]
    public void MinHash_identical_sets_have_equal_signatures()
    {
        var hasher = new MinHasher();
        var set = new HashSet<uint> { 5, 99, 1024, 7777, 13 };
        var s1 = hasher.Signature(set);
        var s2 = hasher.Signature(new HashSet<uint>(set));
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void MinHash_disjoint_sets_have_low_jaccard()
    {
        var a = new HashSet<uint> { 1, 2, 3, 4, 5 };
        var b = new HashSet<uint> { 100, 200, 300, 400, 500 };
        Assert.True(MinHasher.Jaccard(a, b) < 0.1);
    }

    [Fact]
    public void Dsu_unions_transitively()
    {
        var dsu = new Dsu();
        dsu.Union(1, 2);
        dsu.Union(2, 3);
        dsu.MakeSet(4);
        var groups = dsu.Groups();
        Assert.Contains(groups.Values, g => g.OrderBy(x => x).SequenceEqual(new long[] { 1, 2, 3 }));
        Assert.Contains(groups.Values, g => g.Count == 1 && g[0] == 4);
    }

    [Fact]
    public void Representative_oldest_and_shortest_path()
    {
        var files = new List<FileRecord>
        {
            new() { Id = 1, RelPath = "a/b/c/old.jpg", Mtime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new() { Id = 2, RelPath = "new.jpg",       Mtime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) },
        };
        Assert.Equal(1, RepresentativeSelector.Pick(files, RepresentativeRule.Oldest).Id);
        Assert.Equal(2, RepresentativeSelector.Pick(files, RepresentativeRule.ShortestPath).Id);
    }
}
