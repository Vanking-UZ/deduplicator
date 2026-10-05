using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

/// <summary>Тесты безопасных файловых операций (атомарность, отсутствие «половинчатых» файлов).</summary>
public class SafeIoTests : IDisposable
{
    private readonly string _dir;

    public SafeIoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_safeio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void CopyAtomic_copies_content_and_leaves_no_temp()
    {
        var src = Path.Combine(_dir, "src.bin");
        var dst = Path.Combine(_dir, "sub", "dst.bin");
        var data = new byte[123_456];
        new Random(3).NextBytes(data);
        File.WriteAllBytes(src, data);

        SafeIo.CopyAtomic(src, dst);

        Assert.True(File.Exists(dst));
        Assert.Equal(data, File.ReadAllBytes(dst));
        Assert.True(File.Exists(src)); // источник сохранён
        Assert.Empty(Directory.GetFiles(_dir, "*" + SafeIo.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void MoveSafe_same_volume_moves_file()
    {
        var src = Path.Combine(_dir, "a.txt");
        var dst = Path.Combine(_dir, "b.txt");
        File.WriteAllText(src, "hello");

        SafeIo.MoveSafe(src, dst);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal("hello", File.ReadAllText(dst));
    }

    [Fact]
    public void CopyTree_copies_nested_structure()
    {
        var srcRoot = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(srcRoot, "inner"));
        File.WriteAllText(Path.Combine(srcRoot, "root.txt"), "r");
        File.WriteAllText(Path.Combine(srcRoot, "inner", "child.txt"), "c");

        var dstRoot = Path.Combine(_dir, "copy");
        SafeIo.CopyTree(srcRoot, dstRoot);

        Assert.True(File.Exists(Path.Combine(dstRoot, "root.txt")));
        Assert.True(File.Exists(Path.Combine(dstRoot, "inner", "child.txt")));
    }

    [Fact]
    public void MoveTree_into_own_subdirectory_is_rejected()
    {
        var srcRoot = Path.Combine(_dir, "data");
        Directory.CreateDirectory(srcRoot);
        File.WriteAllText(Path.Combine(srcRoot, "f.txt"), "x");
        var dstInside = Path.Combine(srcRoot, "inner", "data");

        Assert.Throws<IOException>(() => SafeIo.MoveTree(srcRoot, dstInside));
        // Источник не тронут.
        Assert.True(File.Exists(Path.Combine(srcRoot, "f.txt")));
    }

    [Fact]
    public void CleanupTempFiles_removes_orphans()
    {
        File.WriteAllText(Path.Combine(_dir, "x.dat" + SafeIo.TempSuffix), "junk");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "keep");

        int removed = SafeIo.CleanupTempFiles(_dir);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
