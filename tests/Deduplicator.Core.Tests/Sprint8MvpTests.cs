using System.Collections.Concurrent;
using FastDedup.Engine;

namespace Deduplicator.Core.Tests;

/// <summary>MVP: универсальный быстрый перечислитель Win32 (FindFirstFileExW) с метаданными.</summary>
public class Sprint8MvpTests : IDisposable
{
    private readonly string _dir;

    public Sprint8MvpTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s8_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Win32FindMetadataEnumerator_matches_directory_enumerate_and_returns_metadata()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Дерево с вложенностью и файлами разного размера.
        Directory.CreateDirectory(Path.Combine(_dir, "sub1", "sub2"));
        Directory.CreateDirectory(Path.Combine(_dir, "sub3"));
        File.WriteAllBytes(Path.Combine(_dir, "a.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_dir, "sub1", "b.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "sub1", "sub2", "c.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_dir, "sub3", "d.bin"), new byte[5]);

        var errors = new ConcurrentBag<ScanError>();
        // MinFileSize=0, чтобы сравнение с Directory.EnumerateFiles было точным (тот не фильтрует по размеру).
        var opts = new ScanOptions { Roots = new[] { _dir }, MinFileSize = 0 };
        var result = new Win32FindMetadataEnumerator().EnumerateMetadata(_dir, opts, errors, default).ToList();

        var expected = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(expected.Count, result.Count); // та же полнота, что у Directory.EnumerateFiles

        foreach (var ef in result)
        {
            Assert.True(File.Exists(ef.FullPath), $"путь должен существовать: {ef.FullPath}");
            Assert.Equal(new FileInfo(ef.FullPath).Length, ef.Size); // размер пришёл из WIN32_FIND_DATA напрямую
            Assert.NotEqual(0L, ef.LastWriteRaw);                     // время записи заполнено (не нужен второй stat)
        }
        Assert.Empty(errors);
    }

    [Fact]
    public void Win32FindMetadataEnumerator_skips_hidden_directories_when_requested()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hidden = Path.Combine(_dir, "hidden");
        Directory.CreateDirectory(hidden);
        File.WriteAllBytes(Path.Combine(_dir, "v.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(hidden, "h.bin"), new byte[3]); // сам файл не скрытый
        new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;

        var errors = new ConcurrentBag<ScanError>();
        var skip = new Win32FindMetadataEnumerator()
            .EnumerateMetadata(_dir, new ScanOptions { Roots = new[] { _dir }, MinFileSize = 0, SkipHiddenSystem = true }, errors, default)
            .Select(f => f.FullPath).ToList();
        Assert.Contains(skip, p => p.EndsWith("v.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(skip, p => p.EndsWith("h.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Win32FindMetadataEnumerator_bad_root_records_error_and_does_not_throw()
    {
        if (!OperatingSystem.IsWindows()) return;

        var errors = new ConcurrentBag<ScanError>();
        // Путь с null-символом гарантированно роняет Path.GetFullPath — раньше это валило весь скан.
        var list = new Win32FindMetadataEnumerator()
            .EnumerateMetadata("bad\0root", new ScanOptions { Roots = new[] { _dir } }, errors, default)
            .ToList();

        Assert.Empty(list);            // ничего не выдали
        Assert.NotEmpty(errors);       // но зафиксировали ошибку этого корня (скан других корней продолжится)
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
