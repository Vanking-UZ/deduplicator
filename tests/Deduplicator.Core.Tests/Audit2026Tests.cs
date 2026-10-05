using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;
using Xunit;

namespace Deduplicator.Core.Tests;

/// <summary>Регрессионные тесты исправлений безопасности данных, стабильности и памяти.</summary>
public sealed class Audit2026Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dedup_a26_" + Guid.NewGuid().ToString("N"));

    public Audit2026Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Mk(string name, byte[]? data = null)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, data ?? new byte[] { 1, 2, 3 });
        return p;
    }

    // B1/откат: цепочка имён (новое имя одного = старое имя другого). Сбой на середине фазы 2 —
    // двухпроходный откат возвращает ВСЕ файлы к исходным именам, сирот нет, журнал удалён.
    [Fact]
    public void Batch_rename_chain_failure_rolls_back_fully_without_orphans()
    {
        var a = Mk("Файл_001.jpg", new byte[] { 1 });
        var b = Mk("Файл_002.jpg", new byte[] { 2 });
        var svc = new BatchRenameService(_root);

        var req = new BatchRenameRequest(new[]
        {
            new RenameSourceFile(1, a, DateTimeOffset.Now, DateTimeOffset.Now, 1, true),
            new RenameSourceFile(2, b, DateTimeOffset.Now, DateTimeOffset.Now, 1, true),
        }, "Файл", 2, 3, RenameSortOrder.Name);

        var plan = svc.BuildPlan(req); // Файл_001→Файл_002 (имя занято членом набора — допустимо), Файл_002→Файл_003
        Assert.True(plan.CanExecute);

        // Блокируем ПОСЛЕДНЕЕ финальное имя уже после построения плана (гонка с внешним процессом).
        var blocker = Mk("Файл_003.jpg", new byte[] { 9 });

        var result = svc.Execute(plan, default);

        Assert.True(result.RolledBack);
        Assert.Empty(result.Orphans);                       // раньше первый же возврат упирался в занятое имя
        Assert.True(File.Exists(a), "первый файл должен вернуться на исходное имя");
        Assert.True(File.Exists(b), "второй файл должен вернуться на исходное имя");
        Assert.Empty(Directory.GetFiles(_root, "rename-journal-*.json")); // полный откат → журнал не нужен
        Assert.Empty(Directory.GetFiles(_root, ".dedup-ren-*"));
        File.Delete(blocker);
    }

    // B1/восстановление: процесс убит посреди фазы 1 — при старте файлы получают исходные имена по журналу.
    [Fact]
    public void Rename_journal_recovers_names_after_simulated_crash()
    {
        var orig = Mk("фото.jpg", new byte[] { 7 });
        var temp = Path.Combine(_root, ".dedup-ren-deadbeef.tmp");
        var final = Path.Combine(_root, "фото_001.jpg");

        RenameJournal.TryBegin(_root, new[] { new RenameJournal.Entry(orig, temp, final) });
        File.Move(orig, temp); // «упали» сразу после первого переименования фазы 1

        var (plans, restored, problems) = RenameJournal.RecoverAll(_root);

        Assert.Equal(1, plans);
        Assert.Equal(1, restored);
        Assert.Empty(problems);
        Assert.True(File.Exists(orig));
        Assert.Empty(Directory.GetFiles(_root, "rename-journal-*.json"));
    }

    // 1.4: партиал-хэш одним чтением байтово идентичен обычному (голова+размер+хвост).
    [Fact]
    public void Partial_hash_single_read_equals_default()
    {
        var data = new byte[600_000];
        new Random(42).NextBytes(data);
        var p = Mk("mid.bin", data);
        var h = new HashService();
        Assert.Equal(
            Convert.ToHexString(h.ComputePartialHash(p, data.Length)),
            Convert.ToHexString(h.ComputePartialHash(p, data.Length, default, preferWholeRead: true)));
    }

    // 1.3: сверка крупным блоком даёт тот же вердикт, что и обычным.
    [Fact]
    public void Byte_equal_with_large_block_matches_default()
    {
        var data = new byte[3_000_000];
        new Random(7).NextBytes(data);
        var a = Mk("a.bin", data);
        var b = Mk("b.bin", data);
        var c = Mk("c.bin", data.Select(x => (byte)(x ^ 1)).ToArray());
        var h = new HashService();
        Assert.True(h.FilesAreByteEqual(a, b, default, 16 << 20));
        Assert.False(h.FilesAreByteEqual(a, c, default, 16 << 20));
    }

    // 1.7: статические квоты — каждая ≥1, сумма вписывается в потолок, толстые урезаются первыми.
    [Fact]
    public void Distribute_cap_keeps_min_one_and_respects_cap()
    {
        var quotas = VolumeParallelExecutor.DistributeCap(
            new[] { (1L, 8), (2L, 1), (3L, 4) }, cap: 6);
        Assert.Equal(3, quotas.Count);
        Assert.All(quotas.Values, v => Assert.True(v >= 1));
        Assert.True(quotas.Values.Sum() <= 6);
        Assert.Equal(1, quotas[2L]); // худой том не расширяется
    }

    // B4: маска облачных атрибутов — Offline/ReparsePoint/RecallOnDataAccess опознаются.
    [Fact]
    public void Cloud_placeholder_attribute_mask_detects_cloud_bits()
    {
        Assert.True(CloudFiles.IsPlaceholder(FileAttributes.Offline));
        Assert.True(CloudFiles.IsPlaceholder(FileAttributes.ReparsePoint | FileAttributes.Archive));
        Assert.True(CloudFiles.IsPlaceholder((FileAttributes)0x00400000)); // RECALL_ON_DATA_ACCESS
        Assert.False(CloudFiles.IsPlaceholder(FileAttributes.Normal));
        Assert.False(CloudFiles.IsPlaceholder(FileAttributes.Hidden | FileAttributes.System));
        // Обычный локальный файл — не плейсхолдер.
        var p = Mk("plain.bin");
        Assert.False(CloudFiles.IsPlaceholderPath(p));
    }

    // B7: у съёмных/сетевых носителей и длинных путей Корзина недоступна — сработает фолбэк на карантин.
    [Fact]
    public void Recycle_availability_rejects_long_and_unc_paths()
    {
        var longPath = @"C:\" + string.Join(@"\", Enumerable.Repeat("папка_длинная", 30)) + @"\файл.bin";
        Assert.True(longPath.Length > 259);
        Assert.False(RecycleBinProbe.CanRecycle(longPath));
        Assert.False(RecycleBinProbe.CanRecycle(@"\\server\share\file.bin"));
    }
}

/// <summary>Доступ к internal RecycleBin.CanRecycle через прокси в том же неймспейсе Core.Services.</summary>
file static class RecycleBinProbe
{
    public static bool CanRecycle(string path) =>
        (bool)typeof(FileOpsService).Assembly
            .GetType("Deduplicator.Core.Services.RecycleBin")!
            .GetMethod("CanRecycle")!
            .Invoke(null, new object[] { path })!;
}
