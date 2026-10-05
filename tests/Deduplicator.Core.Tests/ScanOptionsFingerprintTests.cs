using Deduplicator.Core.Engine;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public sealed class ScanOptionsFingerprintTests
{
    [Fact]
    public void Perceptual_depth_and_runtime_tuning_do_not_force_disk_reenumeration()
    {
        var exact = Options();
        var perceptual = Options();
        perceptual.Depth = ScanDepth.WithSimilarImagesAndAudio;
        perceptual.ImageThreshold = 0.72;
        perceptual.AudioThreshold = 0.61;
        perceptual.Parallelism = 16;
        perceptual.Incremental = false;

        Assert.Equal(ScanOptionsFingerprint.ForEnumeration(exact),
            ScanOptionsFingerprint.ForEnumeration(perceptual));
    }

    [Fact]
    public void Enumeration_filters_change_fingerprint_but_extension_case_and_order_do_not()
    {
        var baseline = Options();
        baseline.ExcludedExtensions = new HashSet<string>(new[] { ".TMP", ".bak" }, StringComparer.OrdinalIgnoreCase);
        var equivalent = Options();
        equivalent.ExcludedExtensions = new HashSet<string>(new[] { ".BAK", ".tmp" }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ScanOptionsFingerprint.ForEnumeration(baseline),
            ScanOptionsFingerprint.ForEnumeration(equivalent));

        equivalent.MinFileSize = 1;
        Assert.NotEqual(ScanOptionsFingerprint.ForEnumeration(baseline),
            ScanOptionsFingerprint.ForEnumeration(equivalent));

        equivalent = Options();
        equivalent.IncludedCategories.Remove(FileCategory.Audio);
        Assert.NotEqual(ScanOptionsFingerprint.ForEnumeration(baseline),
            ScanOptionsFingerprint.ForEnumeration(equivalent));
    }

    private static ScanOptions Options() => new()
    {
        MinFileSize = 0,
        MaxFileSize = null,
        SkipHiddenSystem = true,
        IncludedCategories = Enum.GetValues<FileCategory>().ToHashSet()
    };
}
