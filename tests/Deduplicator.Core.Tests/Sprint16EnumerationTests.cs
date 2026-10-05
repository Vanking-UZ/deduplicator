using System.Collections.Concurrent;
using System.Diagnostics;
using FastDedup.Engine;

namespace Deduplicator.Core.Tests;

/// <summary>
/// 2.11: параллельный обход каталогов Win32Find (главный сценарий — флешки exFAT/FAT32, внешние
/// диски, сеть) и RAM-aware выбор MFT-бэкенда. Главное: параллельный обход даёт ТОТ ЖЕ НАБОР
/// файлов, что старый однопоточный (DOP=1), отмена посреди обхода не зависает, ошибки недоступных
/// каталогов доходят до общего мешка, а оценка памяти MFT — чистая функция с границей 25%.
/// </summary>
public class Sprint16EnumerationTests : IDisposable
{
    private readonly string _dir;

    public Sprint16EnumerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s16_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------- параллельный Win32Find ----------

    /// <summary>Дерево: ветвление 5×4×3×2 (206 каталогов, глубина 5 с корнем), по 5 файлов в каждом.</summary>
    private int BuildTree()
    {
        var levels = new[] { 5, 4, 3, 2 };
        int files = 0;
        void Fill(string dir, int depth)
        {
            for (int f = 0; f < 5; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"file_{depth}_{f}.txt"), "x" + f);
                files++;
            }
            if (depth >= levels.Length) return;
            for (int d = 0; d < levels[depth]; d++)
            {
                var sub = Path.Combine(dir, $"dir_{depth}_{d}");
                Directory.CreateDirectory(sub);
                Fill(sub, depth + 1);
            }
        }
        Fill(_dir, 0);
        return files; // 206 каталогов × 5 = 1030
    }

    private static HashSet<string> Enumerate(string root, int dop, ScanOptions? opts = null,
        ConcurrentBag<ScanError>? errors = null, CancellationToken ct = default)
    {
        opts ??= new ScanOptions { Roots = new[] { root }, MinFileSize = 0 };
        errors ??= new ConcurrentBag<ScanError>();
        return new Win32FindMetadataEnumerator()
            .EnumerateMetadata(root, opts, errors, ct, dop)
            .Select(f => f.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parallel_walk_returns_same_file_set_as_sequential()
    {
        int expected = BuildTree();

        var seq = Enumerate(_dir, dop: 1);
        var par = Enumerate(_dir, dop: 8);

        Assert.Equal(expected, seq.Count);
        Assert.True(par.SetEquals(seq), "набор файлов параллельного обхода разошёлся с однопоточным");
    }

    [Fact]
    public void Parallel_walk_applies_hidden_filter_like_sequential()
    {
        BuildTree();
        // Скрытые файл и каталог с содержимым: SkipHiddenSystem обязан отсечь их в ОБОИХ режимах.
        var hiddenDir = Path.Combine(_dir, "hidden_dir");
        Directory.CreateDirectory(hiddenDir);
        var insideHidden = Path.Combine(hiddenDir, "inside.txt");
        File.WriteAllText(insideHidden, "h");
        File.SetAttributes(hiddenDir, FileAttributes.Directory | FileAttributes.Hidden);
        var hiddenFile = Path.Combine(_dir, "hidden_file.txt");
        File.WriteAllText(hiddenFile, "h");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);

        var opts = new ScanOptions { Roots = new[] { _dir }, MinFileSize = 0, SkipHiddenSystem = true };
        var seq = Enumerate(_dir, 1, opts);
        var par = Enumerate(_dir, 8, opts);

        Assert.True(par.SetEquals(seq), "набор с фильтром скрытых разошёлся между режимами");
        Assert.DoesNotContain(hiddenFile, par);
        Assert.DoesNotContain(insideHidden, par);
    }

    [Fact]
    public void Parallel_walk_applies_excluded_directory_names_like_sequential()
    {
        BuildTree();
        var excluded = Path.Combine(_dir, "node_modules");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "package.txt"), "p");

        var opts = new ScanOptions
        {
            Roots = new[] { _dir },
            MinFileSize = 0,
            ExcludedDirectoryNames = new[] { "node_modules" }
        };
        var seq = Enumerate(_dir, 1, opts);
        var par = Enumerate(_dir, 8, opts);

        Assert.True(par.SetEquals(seq));
        Assert.DoesNotContain(par, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_mid_walk_terminates_without_hang()
    {
        BuildTree();
        using var cts = new CancellationTokenSource();
        var errors = new ConcurrentBag<ScanError>();

        var walk = Task.Run(() =>
        {
            bool cancelled = false;
            int seen = 0;
            try
            {
                foreach (var _ in new Win32FindMetadataEnumerator().EnumerateMetadata(
                             _dir, new ScanOptions { Roots = new[] { _dir }, MinFileSize = 0 },
                             errors, cts.Token, degreeOfParallelism: 8))
                {
                    if (++seen == 40) cts.Cancel(); // отмена ПОСРЕДИ обхода, воркеры ещё работают
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.True(cancelled, "отмена не прервала обход");
        });

        var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == walk, "обход завис после отмены (утечка воркеров)");
        await walk; // пробросить Assert изнутри задачи
    }

    [Fact]
    public void Inaccessible_directory_error_reaches_error_bag_in_both_modes()
    {
        BuildTree();
        var denied = Path.Combine(_dir, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "secret.txt"), "s");

        // Запрет чтения списка (RD) для Everyone — SID *S-1-1-0 не зависит от локали. Если среда
        // не даёт менять ACL (ограниченный CI), кейс честно пропускаем.
        if (RunIcacls($"\"{denied}\" /deny *S-1-1-0:(RD)") != 0) return;
        try
        {
            var errPar = new ConcurrentBag<ScanError>();
            var par = Enumerate(_dir, 8, errors: errPar);
            Assert.Contains(errPar, e => e.Path.StartsWith(denied, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(par, p => p.EndsWith("secret.txt", StringComparison.OrdinalIgnoreCase));

            var errSeq = new ConcurrentBag<ScanError>();
            _ = Enumerate(_dir, 1, errors: errSeq);
            Assert.Contains(errSeq, e => e.Path.StartsWith(denied, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RunIcacls($"\"{denied}\" /remove:d *S-1-1-0"); // иначе temp-каталог не удалится
        }
    }

    private static int RunIcacls(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            return p.WaitForExit(10_000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    // ---------- RAM-aware выбор MFT-бэкенда ----------

    [Fact]
    public void Small_mft_with_plenty_of_ram_allows_mft()
    {
        // 100 МБ MFT ≈ 102 400 записей ≈ 20 МБ карты — при 8 ГБ доступной памяти читаем спокойно.
        Assert.True(MftMemoryPlanner.ShouldUseMft(
            mftValidDataLength: 100L * 1024 * 1024, totalAvailableMemory: 8L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Huge_mft_with_little_ram_falls_back()
    {
        // 10 млн записей (~9,5 ГБ MFT) → прогноз карты ~2 ГБ; на машине с 2 ГБ RAM — только фолбэк.
        Assert.False(MftMemoryPlanner.ShouldUseMft(
            mftValidDataLength: 10_000_000L * 1024, totalAvailableMemory: 2L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Boundary_exactly_quarter_of_available_memory_is_allowed()
    {
        const long records = 1_000_000;
        const long mftLen = records * 1024;
        const long estimate = records * 200; // 200 МБ — прогноз карты

        Assert.Equal(estimate, MftMemoryPlanner.EstimateMapBytes(mftLen));
        Assert.True(MftMemoryPlanner.ShouldUseMft(mftLen, estimate * 4));      // ровно 25% — ещё можно
        Assert.False(MftMemoryPlanner.ShouldUseMft(mftLen, estimate * 4 - 1)); // чуть за границей — нет
    }

    [Theory]
    [InlineData(0L, 8_000_000_000L)]           // размер MFT не узнали
    [InlineData(-5L, 8_000_000_000L)]          // мусор от API
    [InlineData(1_000_000_000L, 0L)]           // память не узнали
    [InlineData(1_000_000_000L, -1L)]
    public void Unknown_or_invalid_inputs_keep_old_behaviour(long mftLen, long ram)
        => Assert.True(MftMemoryPlanner.ShouldUseMft(mftLen, ram)); // нет данных — решаем как раньше

    [Fact]
    public void Low_memory_fallback_notice_is_honest_and_does_not_suggest_admin()
    {
        var d = new BackendDecision(@"D:\", @"D:\", "NTFS", DriveType.Fixed,
            IsElevated: true, IsWholeVolume: true,
            EnumerationBackend.Win32Find, BackendFallbackReason.LowMemory,
            "прогноз карты MFT ≈ 2000 МБ при доступных 2048 МБ");

        var msg = BackendMessages.BuildUserNotice(d);
        Assert.Contains("памяти", msg);              // причина понятна пользователю
        Assert.Contains("совместимый", msg);         // и что скан всё равно идёт
        Assert.DoesNotContain("администратора", msg); // админ тут не поможет — не зовём
    }
}
