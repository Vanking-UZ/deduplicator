using System.Diagnostics;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Сторожевой режим (Sentinel-lite). Проверяется Core-уровень на реальной временной
/// папке и подготовленной БД (строка с известными size/partial/full): (а) копия известного файла
/// в наблюдаемой папке рождает DuplicateFound, уникальный файл и файл того же размера с другим
/// содержимым — нет; (б) файлы меньше 1 КБ игнорируются; (в) Start/Stop идемпотентны и Stop не
/// зависает. UI-часть (тосты WindowNotificationManager) тестами не покрывается — ручная проверка.
/// </summary>
public class Sprint20SentinelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _library;
    private readonly string _watched;
    private readonly FileRepository _files;
    private readonly long _volumeId;
    private readonly HashService _hash = new();

    public Sprint20SentinelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s20_" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_dir, "library");
        _watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_watched);

        var db = new Database(Path.Combine(_dir, "s20.db"));
        db.Initialize();
        _files = new FileRepository(db);
        _volumeId = new VolumeRepository(db).Upsert(new VolumeRecord
        {
            Serial = "S20VOL",
            Label = "Тест",
            MountPath = _dir,
            IsConnected = true
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------------- (а) Детект дубликата / отсутствие ложных срабатываний ----------------

    [Fact]
    public void New_duplicate_in_watched_folder_raises_DuplicateFound()
    {
        var original = WriteRandomFile(Path.Combine(_library, "original.bin"), 200_000, seed: 7);
        IndexFile(original, @"library\original.bin");

        using var svc = new SentinelService(_files, _hash, debounceMs: 250);
        SentinelDuplicateEvent? got = null;
        using var signal = new ManualResetEventSlim(false);
        svc.DuplicateFound += e => { got = e; signal.Set(); };
        svc.Start(new[] { _watched });

        var copyPath = Path.Combine(_watched, "copy.bin");
        File.Copy(original, copyPath);

        Assert.True(signal.Wait(TimeSpan.FromSeconds(10)), "ожидали событие DuplicateFound");
        Assert.Equal(copyPath, got!.FilePath);
        Assert.Equal(1, got.ExistingCount);
        Assert.Equal(200_000, got.Size);
    }

    [Fact]
    public void Unique_file_raises_no_event()
    {
        var original = WriteRandomFile(Path.Combine(_library, "known.bin"), 150_000, seed: 3);
        IndexFile(original, @"library\known.bin");

        using var svc = new SentinelService(_files, _hash, debounceMs: 250);
        using var signal = new ManualResetEventSlim(false);
        svc.DuplicateFound += _ => signal.Set();
        svc.Start(new[] { _watched });

        // Размера 90 000 в индексе нет — воронка обязана закончиться на дешёвом CountBySize.
        WriteRandomFile(Path.Combine(_watched, "unique.bin"), 90_000, seed: 4);

        Assert.False(signal.Wait(TimeSpan.FromSeconds(2.5)), "уникальный файл не должен рождать событие");
    }

    [Fact]
    public void Same_size_different_content_raises_no_event()
    {
        var original = WriteRandomFile(Path.Combine(_library, "known2.bin"), 150_000, seed: 11);
        IndexFile(original, @"library\known2.bin");

        using var svc = new SentinelService(_files, _hash, debounceMs: 250);
        using var signal = new ManualResetEventSlim(false);
        svc.DuplicateFound += _ => signal.Set();
        svc.Start(new[] { _watched });

        // Размер совпал, содержимое другое — частичный хэш обязан отсеять кандидата.
        WriteRandomFile(Path.Combine(_watched, "same_size.bin"), 150_000, seed: 99);

        Assert.False(signal.Wait(TimeSpan.FromSeconds(2.5)), "файл того же размера с другим содержимым — не дубликат");
    }

    // ---------------- (б) Файлы меньше 1 КБ игнорируются ----------------

    [Fact]
    public void File_under_1kb_is_ignored()
    {
        // Даже байт-идентичная копия проиндексированного файла < 1 КБ не должна рождать событие.
        var small = WriteRandomFile(Path.Combine(_library, "small.bin"), 512, seed: 5);
        IndexFile(small, @"library\small.bin");

        using var svc = new SentinelService(_files, _hash, debounceMs: 250);
        using var signal = new ManualResetEventSlim(false);
        svc.DuplicateFound += _ => signal.Set();
        svc.Start(new[] { _watched });

        File.Copy(small, Path.Combine(_watched, "small_copy.bin"));

        Assert.False(signal.Wait(TimeSpan.FromSeconds(2.5)), "файлы меньше 1 КБ сторож игнорирует");
    }

    // ---------------- (в) Start/Stop идемпотентны, Stop не виснет ----------------

    [Fact]
    public void Start_and_stop_are_idempotent_and_stop_does_not_hang()
    {
        using var svc = new SentinelService(_files, _hash, debounceMs: 200);

        svc.Start(new[] { _watched });
        svc.Start(new[] { _watched }); // повторный старт — безопасный перезапуск, не исключение
        Assert.True(svc.IsRunning);

        var sw = Stopwatch.StartNew();
        svc.Stop();
        svc.Stop(); // повторный стоп — no-op
        sw.Stop();
        Assert.False(svc.IsRunning);
        Assert.True(sw.ElapsedMilliseconds < 5000, "Stop не должен зависать");

        // Несуществующая папка: не бросает и слежение не считается запущенным.
        svc.Start(new[] { Path.Combine(_dir, "no_such_dir") });
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void After_stop_no_events_are_raised()
    {
        var original = WriteRandomFile(Path.Combine(_library, "known3.bin"), 120_000, seed: 21);
        IndexFile(original, @"library\known3.bin");

        using var svc = new SentinelService(_files, _hash, debounceMs: 200);
        using var signal = new ManualResetEventSlim(false);
        svc.DuplicateFound += _ => signal.Set();
        svc.Start(new[] { _watched });
        svc.Stop();

        File.Copy(original, Path.Combine(_watched, "late_copy.bin"));

        Assert.False(signal.Wait(TimeSpan.FromSeconds(2)), "после Stop событий быть не должно");
    }

    // ---------------- Вспомогательные ----------------

    private static string WriteRandomFile(string path, int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Кладёт в БД слепок файла с посчитанными partial/full — как после полного скана.</summary>
    private void IndexFile(string path, string relPath)
    {
        long size = new FileInfo(path).Length;
        _files.UpsertMetaBatch(new[]
        {
            new FileRecord
            {
                VolumeId = _volumeId,
                RelPath = relPath,
                Name = Path.GetFileName(path),
                Ext = Path.GetExtension(path),
                Size = size,
                Mtime = DateTimeOffset.Now,
                Category = FileCategory.Other,
                PartialHash = _hash.ComputePartialHash(path, size),
                PartialVer = HashService.CurrentPartialVersion,
                FullHash = _hash.ComputeFullHash(path),
                ScanState = ScanState.Full
            }
        });
    }
}
