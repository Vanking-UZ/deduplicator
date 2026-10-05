using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Проводник: перенос/копирование на другой диск. FileTransferService обязан:
/// (а) копировать файлы и папки байт-в-байт, не трогая источник;
/// (б) при переносе удалять источник ТОЛЬКО после полной проверки копии; любой сбой посреди —
///     источник цел целиком (включая уже скопированные файлы), список ошибок честный;
/// (в) конфликт имён решать суффиксом « (2)» и никогда не перезаписывать существующее;
/// (г) при отмене — дописать текущий файл и остановиться: скопированное на месте,
///     неначатое не тронуто, неперенесённые источники целы.
/// Межтомовый путь (копия → проверка хэшем → удаление) прогоняется внутри одного временного
/// тома через подмену определения «тот же том» (sameVolumeProbe => false).
/// </summary>
public class Sprint25TransferTests : IDisposable
{
    private readonly string _dir;

    public Sprint25TransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s25_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Сервис с принудительно межтомовым путём (копия → проверка → удаление).</summary>
    private static FileTransferService CrossVolumeService() => new((_, _) => false);

    private string MakeFile(string relPath, byte[] content)
    {
        var path = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Rnd(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Синхронный IProgress: колбэк выполняется прямо в потоке операции (без SynchronizationContext).</summary>
    private sealed class SyncProgress : IProgress<TransferProgress>
    {
        private readonly Action<TransferProgress> _handler;
        public SyncProgress(Action<TransferProgress> handler) => _handler = handler;
        public void Report(TransferProgress value) => _handler(value);
    }

    // ---------------- (а) Копирование: байт-в-байт, источник цел ----------------

    [Fact]
    public async Task Copy_file_and_folder_copies_bytes_and_keeps_source()
    {
        var fileContent = Rnd(50_000, 1);
        var innerContent = Rnd(30_000, 2);
        var deepContent = Rnd(10_000, 3);
        var file = MakeFile("src\\solo.bin", fileContent);
        MakeFile("src\\pack\\inner.bin", innerContent);
        MakeFile("src\\pack\\deep\\leaf.bin", deepContent);
        var pack = Path.Combine(_dir, "src", "pack");
        var dest = Path.Combine(_dir, "dest");

        var svc = CrossVolumeService();
        var result = await svc.TransferAsync(new[]
        {
            new TransferItem(file, IsFolder: false),
            new TransferItem(pack, IsFolder: true)
        }, dest, move: false);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.Moved);
        Assert.Equal(3, result.FilesTransferred);
        Assert.Empty(result.Failed);
        Assert.False(result.Cancelled);

        // Копии байт-в-байт, включая вложенность.
        Assert.Equal(fileContent, File.ReadAllBytes(Path.Combine(dest, "solo.bin")));
        Assert.Equal(innerContent, File.ReadAllBytes(Path.Combine(dest, "pack", "inner.bin")));
        Assert.Equal(deepContent, File.ReadAllBytes(Path.Combine(dest, "pack", "deep", "leaf.bin")));

        // Источник не тронут.
        Assert.Equal(fileContent, File.ReadAllBytes(file));
        Assert.Equal(innerContent, File.ReadAllBytes(Path.Combine(pack, "inner.bin")));
        Assert.Equal(deepContent, File.ReadAllBytes(Path.Combine(pack, "deep", "leaf.bin")));

        // Временных файлов не осталось.
        Assert.Empty(Directory.GetFiles(dest, "*" + SafeIo.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Copy_preserves_file_dates()
    {
        var src = MakeFile("dated.bin", Rnd(1000, 4));
        var stamp = new DateTime(2020, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, stamp);

        var dest = Path.Combine(_dir, "dest_dates");
        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(src, false) }, dest, move: false);

        Assert.Empty(result.Failed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dest, "dated.bin")));
    }

    [Fact]
    public async Task Copy_of_empty_folder_creates_empty_directory()
    {
        var empty = Path.Combine(_dir, "hollow");
        Directory.CreateDirectory(empty);
        var dest = Path.Combine(_dir, "dest_empty");

        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(empty, true) }, dest, move: false);

        Assert.Equal(1, result.Copied);
        Assert.Empty(result.Failed);
        Assert.True(Directory.Exists(Path.Combine(dest, "hollow")));
        Assert.True(Directory.Exists(empty)); // источник цел
    }

    // ---------------- (б) Перенос: источник удаляется только после проверки ----------------

    [Fact]
    public async Task Move_folder_cross_volume_verifies_then_removes_source()
    {
        var a = Rnd(20_000, 5);
        var b = Rnd(40_000, 6);
        MakeFile("mv\\a.bin", a);
        MakeFile("mv\\sub\\b.bin", b);
        var src = Path.Combine(_dir, "mv");
        var dest = Path.Combine(_dir, "dest_mv");

        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(src, true) }, dest, move: true);

        Assert.Equal(1, result.Moved);
        Assert.Equal(2, result.FilesTransferred);
        Assert.Empty(result.Failed);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(dest, "mv", "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(dest, "mv", "sub", "b.bin")));
        Assert.False(Directory.Exists(src)); // источник удалён (после полной проверки)
    }

    [Fact]
    public async Task Move_folder_with_locked_file_keeps_entire_source_intact()
    {
        var okContent = Rnd(10_000, 7);
        var lockedContent = Rnd(10_000, 8);
        var ok = MakeFile("mvfail\\a_ok.bin", okContent);
        var locked = MakeFile("mvfail\\b_locked.bin", lockedContent);
        var src = Path.Combine(_dir, "mvfail");
        var dest = Path.Combine(_dir, "dest_mvfail");

        TransferResult result;
        // «Файл занят другой программой»: держим эксклюзивную блокировку на время переноса.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await CrossVolumeService().TransferAsync(
                new[] { new TransferItem(src, true) }, dest, move: true);
        }

        Assert.Equal(0, result.Moved);
        Assert.Single(result.Failed);
        Assert.Contains("mvfail", result.Failed[0].SourcePath);

        // КЛЮЧЕВОЕ: источник цел ЦЕЛИКОМ — даже файл, который уже успели скопировать,
        // не удалён (удаление разрешено только после полной проверки всего дерева).
        Assert.Equal(okContent, File.ReadAllBytes(ok));
        Assert.Equal(lockedContent, File.ReadAllBytes(locked));

        // Недокопированная половинка в назначении не осталась.
        Assert.False(Directory.Exists(Path.Combine(dest, "mvfail")));
        Assert.Equal(0, result.FilesTransferred);
    }

    [Fact]
    public async Task Move_file_with_busy_destination_fails_that_item_and_continues_others()
    {
        var aContent = Rnd(5_000, 9);
        var bContent = Rnd(5_000, 10);
        var a = MakeFile("iso\\a.bin", aContent);
        var b = MakeFile("iso\\b.bin", bContent);
        var dest = Path.Combine(_dir, "dest_iso");
        Directory.CreateDirectory(dest);

        TransferResult result;
        // «Назначение занято»: блокируем временный файл, в который пишет атомарное копирование.
        var tmp = Path.Combine(dest, "a.bin" + SafeIo.TempSuffix);
        using (new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            result = await CrossVolumeService().TransferAsync(new[]
            {
                new TransferItem(a, false),
                new TransferItem(b, false)
            }, dest, move: true);
        }

        // Первый элемент честно в ошибках, второй перенесён — пакет не упал.
        Assert.Single(result.Failed);
        Assert.Equal(a, result.Failed[0].SourcePath);
        Assert.Equal(1, result.Moved);
        Assert.Equal(aContent, File.ReadAllBytes(a));                    // источник сбойного цел
        Assert.False(File.Exists(b));                                     // успешный перенесён
        Assert.Equal(bContent, File.ReadAllBytes(Path.Combine(dest, "b.bin")));
    }

    [Fact]
    public async Task Move_readonly_file_cross_volume_succeeds_and_keeps_attribute()
    {
        var content = Rnd(2_000, 11);
        var src = MakeFile("ro.bin", content);
        File.SetAttributes(src, File.GetAttributes(src) | FileAttributes.ReadOnly);
        var dest = Path.Combine(_dir, "dest_ro");

        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(src, false) }, dest, move: true);

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Moved);
        Assert.False(File.Exists(src));
        var dst = Path.Combine(dest, "ro.bin");
        Assert.Equal(content, File.ReadAllBytes(dst));
        Assert.True(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly)); // атрибуты переехали
        File.SetAttributes(dst, FileAttributes.Normal); // чтобы уборка временной папки прошла
    }

    [Fact]
    public async Task Move_folder_into_its_own_subfolder_fails_item_and_source_intact()
    {
        var content = Rnd(1_000, 12);
        MakeFile("matryoshka\\x.bin", content);
        var src = Path.Combine(_dir, "matryoshka");
        var destInside = Path.Combine(src, "inner");

        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(src, true) }, destInside, move: true);

        Assert.Single(result.Failed);
        Assert.Contains("внутрь самой себя", result.Failed[0].Message);
        Assert.Equal(0, result.Moved);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(src, "x.bin")));
    }

    [Fact]
    public async Task Move_file_to_its_own_folder_is_skipped()
    {
        var content = Rnd(1_000, 13);
        var src = MakeFile("samehome\\here.bin", content);
        var home = Path.Combine(_dir, "samehome");

        var result = await CrossVolumeService().TransferAsync(
            new[] { new TransferItem(src, false) }, home, move: true);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Moved);
        Assert.Empty(result.Failed);
        Assert.Equal(content, File.ReadAllBytes(src)); // файл не тронут и не размножен
        Assert.Single(Directory.GetFiles(home));
    }

    // ---------------- (в) Конфликты имён: суффикс « (2)», никогда не перезаписывать ----------------

    [Fact]
    public async Task Name_conflict_appends_suffix_and_never_overwrites()
    {
        var original = Rnd(3_000, 14);  // уже лежит в назначении — должен уцелеть
        var incoming = Rnd(3_000, 15);
        var dest = Path.Combine(_dir, "dest_conflict");
        Directory.CreateDirectory(Path.Combine(dest, "pack"));
        File.WriteAllBytes(Path.Combine(dest, "a.txt"), original);
        File.WriteAllBytes(Path.Combine(dest, "pack", "old.bin"), original);

        var src = MakeFile("confl\\a.txt", incoming);
        MakeFile("confl\\pack\\new.bin", incoming);
        var packSrc = Path.Combine(_dir, "confl", "pack");

        var svc = CrossVolumeService();
        var items = new[] { new TransferItem(src, false), new TransferItem(packSrc, true) };
        var r1 = await svc.TransferAsync(items, dest, move: false);
        var r2 = await svc.TransferAsync(items, dest, move: false);

        Assert.Empty(r1.Failed);
        Assert.Empty(r2.Failed);

        // Существовавшее в назначении не перезаписано.
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dest, "a.txt")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dest, "pack", "old.bin")));

        // Суффикс перед расширением: «a (2).txt», «a (3).txt»; папки: «pack (2)», «pack (3)».
        Assert.Equal(incoming, File.ReadAllBytes(Path.Combine(dest, "a (2).txt")));
        Assert.Equal(incoming, File.ReadAllBytes(Path.Combine(dest, "a (3).txt")));
        Assert.Equal(incoming, File.ReadAllBytes(Path.Combine(dest, "pack (2)", "new.bin")));
        Assert.Equal(incoming, File.ReadAllBytes(Path.Combine(dest, "pack (3)", "new.bin")));
    }

    // ---------------- (г) Отмена: дописали текущий файл и остановились ----------------

    [Fact]
    public async Task Cancellation_between_files_keeps_done_work_and_unmoved_sources()
    {
        var c1 = Rnd(8_000, 16);
        var c2 = Rnd(8_000, 17);
        var c3 = Rnd(8_000, 18);
        var f1 = MakeFile("cancel\\f1.bin", c1);
        var f2 = MakeFile("cancel\\f2.bin", c2);
        var f3 = MakeFile("cancel\\f3.bin", c3);
        var dest = Path.Combine(_dir, "dest_cancel");

        using var cts = new CancellationTokenSource();
        // Отменяем, как только первый файл полностью готов (Done == 1).
        var progress = new SyncProgress(p => { if (p.Done >= 1) cts.Cancel(); });

        var result = await CrossVolumeService().TransferAsync(new[]
        {
            new TransferItem(f1, false),
            new TransferItem(f2, false),
            new TransferItem(f3, false)
        }, dest, move: true, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Moved);

        // Уже перенесённое — на месте в назначении.
        Assert.Equal(c1, File.ReadAllBytes(Path.Combine(dest, "f1.bin")));
        Assert.False(File.Exists(f1));

        // Неначатое не тронуто: источники целы, в назначении их нет.
        Assert.Equal(c2, File.ReadAllBytes(f2));
        Assert.Equal(c3, File.ReadAllBytes(f3));
        Assert.False(File.Exists(Path.Combine(dest, "f2.bin")));
        Assert.False(File.Exists(Path.Combine(dest, "f3.bin")));
    }

    // ---------------- Оценка и низкоуровневый MoveTree ----------------

    [Fact]
    public void Estimate_counts_files_folders_and_bytes()
    {
        var f1 = MakeFile("est\\one.bin", new byte[10]);
        var f2 = MakeFile("est\\two.bin", new byte[20]);
        MakeFile("est\\pack\\a.bin", new byte[1]);
        MakeFile("est\\pack\\b.bin", new byte[2]);
        MakeFile("est\\pack\\deep\\c.bin", new byte[3]);
        Directory.CreateDirectory(Path.Combine(_dir, "est", "pack", "void"));
        var pack = Path.Combine(_dir, "est", "pack");

        var est = CrossVolumeService().Estimate(new[]
        {
            new TransferItem(f1, false),
            new TransferItem(f2, false),
            new TransferItem(pack, true)
        });

        Assert.Equal(2, est.Files);
        Assert.Equal(1, est.Folders);
        Assert.Equal(36, est.TotalBytes);
        Assert.Equal(5, est.TotalUnits); // 2 файла + 3 файла внутри папки
    }

    [Fact]
    public void MoveTree_force_cross_volume_verifies_every_file_and_prunes_source()
    {
        var a = Rnd(15_000, 19);
        var b = Rnd(25_000, 20);
        MakeFile("tree\\a.bin", a);
        MakeFile("tree\\deep\\b.bin", b);
        Directory.CreateDirectory(Path.Combine(_dir, "tree", "emptydir"));
        var src = Path.Combine(_dir, "tree");
        var dst = Path.Combine(_dir, "tree_moved");

        var copied = new List<string>();
        bool fullyRemoved = SafeIo.MoveTree(src, dst, default,
            (s, _) => copied.Add(Path.GetFileName(s)), forceCrossVolume: true);

        Assert.True(fullyRemoved);
        Assert.False(Directory.Exists(src));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(dst, "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(dst, "deep", "b.bin")));
        Assert.True(Directory.Exists(Path.Combine(dst, "emptydir"))); // пустые папки переезжают
        Assert.Equal(2, copied.Count); // прогресс по каждому файлу
    }
}
