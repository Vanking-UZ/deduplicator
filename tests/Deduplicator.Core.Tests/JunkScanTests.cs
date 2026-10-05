using System.Linq;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

public class JunkScanTests : IDisposable
{
    private readonly string _dir;

    public JunkScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_junk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Finds_empty_folders_zero_byte_and_temp_files()
    {
        File.WriteAllText(Path.Combine(_dir, "normal.txt"), "data");
        File.WriteAllBytes(Path.Combine(_dir, "zero.dat"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(_dir, "scratch.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "emptyA"));
        Directory.CreateDirectory(Path.Combine(_dir, "emptyB", "emptyC")); // вложенные пустые

        var svc = new JunkScanService();
        var items = svc.Scan(new JunkScanOptions
        {
            Roots = { _dir },
            EmptyFolders = true,
            ZeroByteFiles = true,
            TempFiles = true,
            BrokenShortcuts = false,
            LargeFiles = false
        }, null, default);

        var empty = items.Where(i => i.Kind == JunkKind.EmptyFolder).Select(i => i.Path).ToList();
        // Только верхние пустые папки: emptyA и emptyB, но не вложенная emptyC.
        Assert.Contains(empty, p => p.EndsWith("emptyA"));
        Assert.Contains(empty, p => p.EndsWith("emptyB"));
        Assert.DoesNotContain(empty, p => p.EndsWith("emptyC"));

        Assert.Contains(items, i => i.Kind == JunkKind.ZeroByteFile && i.Path.EndsWith("zero.dat"));
        Assert.Contains(items, i => i.Kind == JunkKind.TempFile && i.Path.EndsWith("scratch.tmp"));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("normal.txt"));
    }

    [Fact]
    public void Large_files_detected_only_when_enabled()
    {
        File.WriteAllBytes(Path.Combine(_dir, "big.bin"), new byte[2048]);

        var svc = new JunkScanService();
        var items = svc.Scan(new JunkScanOptions
        {
            Roots = { _dir },
            EmptyFolders = false, ZeroByteFiles = false, TempFiles = false, BrokenShortcuts = false,
            LargeFiles = true,
            LargeThresholdBytes = 1024
        }, null, default);

        Assert.Contains(items, i => i.Kind == JunkKind.LargeFile && i.Path.EndsWith("big.bin"));
    }

    [Fact]
    public void Large_temp_file_is_returned_once_and_keeps_large_file_safety_kind()
    {
        var path = Path.Combine(_dir, "archive.tmp");
        File.WriteAllBytes(path, new byte[2048]);

        var items = new JunkScanService().Scan(new JunkScanOptions
        {
            Roots = { _dir },
            EmptyFolders = false,
            ZeroByteFiles = true,
            TempFiles = true,
            BrokenShortcuts = false,
            LargeFiles = true,
            LargeThresholdBytes = 1024
        }, null, default);

        var match = Assert.Single(items.Where(i =>
            string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(JunkKind.LargeFile, match.Kind);
    }

    [Fact]
    public void Scan_with_broken_shortcuts_enabled_does_not_throw()
    {
        // Файл .lnk с мусором + обычные файлы — поиск битых ярлыков не должен валить скан (#8).
        File.WriteAllText(Path.Combine(_dir, "garbage.lnk"), "this is not a real shortcut");
        File.WriteAllText(Path.Combine(_dir, "normal.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "a.tmp"), "t");

        var svc = new JunkScanService();
        var ex = Record.Exception(() => svc.Scan(new JunkScanOptions
        {
            Roots = { _dir },
            EmptyFolders = true,
            ZeroByteFiles = true,
            TempFiles = true,
            BrokenShortcuts = true,
            LargeFiles = true
        }, null, default));

        Assert.Null(ex);
    }

    [Fact]
    public void Progress_is_monotonic_and_finishes_at_one_hundred_percent()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "progress")).FullName;
        for (int i = 0; i < 420; i++) File.WriteAllText(Path.Combine(folder, $"{i}.tmp"), "data");
        var updates = new List<JunkScanProgress>();

        new JunkScanService().Scan(new JunkScanOptions
        {
            Roots = { folder },
            EmptyFolders = false,
            ZeroByteFiles = false,
            TempFiles = true,
            BrokenShortcuts = false,
            LargeFiles = false
        }, updates.Add, default);

        Assert.NotEmpty(updates);
        Assert.Equal(420, updates[^1].FilesVisited);
        Assert.Equal(1.0, updates[^1].Fraction);
        var fractions = updates.Where(x => x.Fraction.HasValue).Select(x => x.Fraction!.Value).ToList();
        Assert.True(fractions.SequenceEqual(fractions.OrderBy(x => x)));
    }

    [Fact]
    public void Deep_tree_is_scanned_iteratively_without_crash()
    {
        // Очень глубокое дерево (через \\?\ обходим лимит 260). Итеративный обход не должен
        // падать (рекурсия рисковала бы StackOverflow), и должен дойти до файла на дне (#8).
        const int depth = 300;
        var deepRoot = Path.Combine(_dir, "d");
        var path = _dir;
        for (int i = 0; i < depth; i++)
        {
            path = Path.Combine(path, "d");
            Directory.CreateDirectory(LongPath.Fix(path));
        }
        File.WriteAllText(LongPath.Fix(Path.Combine(path, "scratch.tmp")), "x");

        try
        {
            var svc = new JunkScanService();
            List<JunkItem>? items = null;
            var ex = Record.Exception(() => items = svc.Scan(new JunkScanOptions
            {
                Roots = { _dir },
                EmptyFolders = true, ZeroByteFiles = true, TempFiles = true,
                BrokenShortcuts = false, LargeFiles = false
            }, null, default));

            Assert.Null(ex);                       // не упало
            Assert.NotNull(items);
            Assert.Contains(items!, i => i.Kind == JunkKind.TempFile && i.Path.EndsWith("scratch.tmp"));
            Assert.DoesNotContain(items!, i => i.Kind == JunkKind.EmptyFolder); // все папки на пути не пусты
        }
        finally
        {
            try { Directory.Delete(LongPath.Fix(deepRoot), true); } catch { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(LongPath.Fix(_dir), true); } catch { }
    }
}
