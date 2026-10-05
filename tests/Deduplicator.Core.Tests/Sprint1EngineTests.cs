using System.Diagnostics;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Базовые гарантии движка точных дубликатов:
/// стабильность размера, безопасность побайтовой сверки, план чтения на том, троттлинг прогресса.
/// </summary>
public class Sprint1EngineTests : IDisposable
{
    private readonly string _dir;
    private readonly HashService _hash = new();

    public Sprint1EngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(string name, byte[] data)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    // ---------------- Стабильность размера (P0) ----------------

    [Fact]
    public void FullHash_with_correct_size_matches_plain_full_hash()
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data);
        var p = Write("a.bin", data);

        Assert.Equal(_hash.ComputeFullHash(p), _hash.ComputeFullHash(p, data.Length));
    }

    [Fact]
    public void FullHash_throws_when_expected_size_differs_metadata_stale()
    {
        var data = new byte[10_000];
        new Random(3).NextBytes(data);
        var p = Write("b.bin", data);

        // Размер в metadata был другим (файл изменился до чтения) → не доверяем.
        Assert.Throws<FileChangedDuringScanException>(() => _hash.ComputeFullHash(p, data.Length + 1));
    }

    [Fact]
    public void FullHash_throws_when_file_grew_after_metadata()
    {
        var p = Write("grow.bin", new byte[1000]);
        long sizeAtMetadata = new FileInfo(p).Length;

        // Файл вырос между metadata-этапом и чтением (активно пишущийся лог и т.п.).
        using (var fs = new FileStream(p, FileMode.Append)) fs.Write(new byte[500]);

        var ex = Assert.Throws<FileChangedDuringScanException>(() => _hash.ComputeFullHash(p, sizeAtMetadata));
        Assert.Equal(sizeAtMetadata, ex.ExpectedSize);
        Assert.Equal(1500, ex.ActualSize);
    }

    [Fact]
    public void PartialHash_throws_when_large_file_size_differs()
    {
        var data = new byte[300_000];
        new Random(5).NextBytes(data);
        var p = Write("big.bin", data);

        Assert.Throws<FileChangedDuringScanException>(() => _hash.ComputePartialHash(p, data.Length - 1));
        // Корректный размер — без исключения.
        Assert.NotNull(_hash.ComputePartialHash(p, data.Length));
    }

    // ---------------- Безопасность побайтовой сверки (P0) ----------------

    private sealed class ThrowOnCompareHash : IHashService
    {
        public byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default, bool preferWholeRead = false)
            => throw new NotSupportedException();
        public byte[] ComputeFullHash(string fullPath, CancellationToken ct = default)
            => throw new NotSupportedException();
        public byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default)
            => throw new NotSupportedException();
        // Файлы доступны, но прочитать не получается (заблокирован/IO-ошибка).
        public bool FilesAreByteEqual(string a, string b, CancellationToken ct = default, int blockBytes = 0)
            => throw new IOException("read failure");
    }

    [Fact]
    public void Byte_compare_exception_does_not_group_connected_files()
    {
        var (files, groups, lsh, vol) = SetupTwoIdenticalConnectedFiles();
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };

        // Сверка байтов бросает исключение, оба файла подключены → НЕ доказано равенство → 0 групп.
        var grouper = new DuplicateGrouper(files, groups, lsh, new ThrowOnCompareHash());
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(0, created);
        Assert.Empty(groups.GetGroups(MatchType.Exact));
    }

    [Fact]
    public void Byte_compare_success_groups_connected_files()
    {
        var (files, groups, lsh, vol) = SetupTwoIdenticalConnectedFiles();
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };

        // Контроль: реальная (успешная) сверка → группа создаётся.
        var grouper = new DuplicateGrouper(files, groups, lsh, new HashService());
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(1, created);
        Assert.Single(groups.GetGroups(MatchType.Exact));
    }

    private (FileRepository, GroupRepository, LshRepository, VolumeRecord) SetupTwoIdenticalConnectedFiles()
    {
        var data = new byte[120_000];
        new Random(13).NextBytes(data);
        var a = Write("a.bin", data);
        var b = Write("b.bin", (byte[])data.Clone());

        var db = new Database(Path.Combine(_dir, "cmp_" + Guid.NewGuid().ToString("N") + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();

        var vol = new VolumeRecord { Serial = "CMPVOL", Label = "Тест", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        // Одинаковый полный хэш у обоих → они кандидаты на точный дубль (сверяются байтами).
        var fullHash = hash.ComputeFullHash(a);
        var records = new List<FileRecord>
        {
            NewRec(vol.Id, Path.GetFileName(a), data.Length, hash.ComputePartialHash(a, data.Length), fullHash),
            NewRec(vol.Id, Path.GetFileName(b), data.Length, hash.ComputePartialHash(b, data.Length), fullHash),
        };
        files.UpsertMetaBatch(records);
        return (files, groups, lsh, vol);
    }

    private static FileRecord NewRec(long volId, string name, long size, byte[]? partial, byte[] full) => new()
    {
        VolumeId = volId,
        RelPath = name,
        Name = name,
        Ext = Path.GetExtension(name),
        Size = size,
        Mtime = DateTimeOffset.Now,
        Category = FileCategory.Other,
        PartialHash = partial,
        FullHash = full,
        ScanState = ScanState.Full
    };

    // Бросает на сверке, если путь оканчивается на needle; иначе — настоящая сверка.
    private sealed class ThrowForPathHash : IHashService
    {
        private readonly HashService _real = new();
        private readonly string _needle;
        public ThrowForPathHash(string needle) => _needle = needle;
        public byte[] ComputePartialHash(string p, long s, CancellationToken ct = default, bool preferWholeRead = false) => _real.ComputePartialHash(p, s, ct, preferWholeRead);
        public byte[] ComputeFullHash(string p, CancellationToken ct = default) => _real.ComputeFullHash(p, ct);
        public byte[] ComputeFullHash(string p, long s, CancellationToken ct = default) => _real.ComputeFullHash(p, s, ct);
        public bool FilesAreByteEqual(string a, string b, CancellationToken ct = default, int blockBytes = 0)
        {
            if (a.EndsWith(_needle, StringComparison.OrdinalIgnoreCase) || b.EndsWith(_needle, StringComparison.OrdinalIgnoreCase))
                throw new IOException("simulated unreadable: " + _needle);
            return _real.FilesAreByteEqual(a, b, ct);
        }
    }

    [Fact]
    public void Unreadable_head_does_not_split_real_duplicate_pair()
    {
        // A и C байт-идентичны; B читается, но сверка с ним падает (как нечитаемый). Порядок B,A,C —
        // B становится «головой». Раньше A и C оба отбрасывались; теперь A==C всё равно группируются.
        var data = new byte[120_000];
        new Random(21).NextBytes(data);
        var b = Write("throwfile.bin", new byte[] { 1, 2, 3 });
        var a = Write("a.bin", data);
        var c = Write("c.bin", (byte[])data.Clone());

        var db = new Database(Path.Combine(_dir, "split.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var realHash = new HashService();

        var vol = new VolumeRecord { Serial = "SPL", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        var sharedHash = realHash.ComputeFullHash(a); // один и тот же full_hash у всех трёх
        files.UpsertMetaBatch(new List<FileRecord>
        {
            NewRec(vol.Id, "throwfile.bin", 3, realHash.ComputePartialHash(b, 3), sharedHash),
            NewRec(vol.Id, "a.bin", data.Length, realHash.ComputePartialHash(a, data.Length), sharedHash),
            NewRec(vol.Id, "c.bin", data.Length, realHash.ComputePartialHash(c, data.Length), sharedHash),
        });

        var grouper = new DuplicateGrouper(files, groups, lsh, new ThrowForPathHash("throwfile.bin"));
        int created = grouper.BuildExactGroups(new Dictionary<long, VolumeRecord> { [vol.Id] = vol },
            _ => true, RepresentativeRule.Oldest, null, default);

        Assert.Equal(1, created); // A и C сгруппированы, несмотря на нечитаемую «голову» B
        var g = Assert.Single(groups.GetGroups(MatchType.Exact));
        Assert.Equal(2, g.Members.Count);     // ровно A и C, без B
        Assert.Equal(1.0, g.Confidence);      // оба члена сверены байтами → полностью доверенная группа
    }

    [Fact]
    public void Offline_head_does_not_merge_byte_different_connected_files()
    {
        // Коллизия хэша: A и C ПОДКЛЮЧЕНЫ и БАЙТ-РАЗЛИЧНЫ, но имеют одинаковый сохранённый full_hash.
        // B на отключённом томе с тем же хэшем и идёт ПЕРВЫМ (был бы «головой»). Раньше офлайн-голова
        // коротко замыкала сверку и объединяла A и C в группу 1.0 → риск удаления разных данных.
        var a = Write("a.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var c = Write("c.bin", new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }); // другое содержимое

        var db = new Database(Path.Combine(_dir, "merge.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var realHash = new HashService();

        var vol1 = new VolumeRecord { Serial = "ON", MountPath = _dir, IsConnected = true };
        vol1.Id = volumes.Upsert(vol1);
        var vol2 = new VolumeRecord { Serial = "OFF", MountPath = null, IsConnected = false };
        vol2.Id = volumes.Upsert(vol2);

        var collidingHash = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11 };
        files.UpsertMetaBatch(new List<FileRecord>
        {
            NewRec(vol2.Id, "b_offline.bin", 8, null, collidingHash), // офлайн, идёт первым
            NewRec(vol1.Id, "a.bin", 8, realHash.ComputePartialHash(a, 8), collidingHash),
            NewRec(vol1.Id, "c.bin", 8, realHash.ComputePartialHash(c, 8), collidingHash),
        });

        var dict = new Dictionary<long, VolumeRecord> { [vol1.Id] = vol1, [vol2.Id] = vol2 };
        var grouper = new DuplicateGrouper(files, groups, lsh, new HashService());
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default);

        // Ровно одна группа (A + офлайн B как hash-only); C НЕ объединён с A, т.к. байты различны.
        Assert.Equal(1, created);
        var g = Assert.Single(groups.GetGroups(MatchType.Exact));
        Assert.Equal(2, g.Members.Count);
        Assert.True(g.Confidence < 1.0, "группа с непроверяемым офлайн-членом должна быть hash-only (<1.0)");
    }

    // ---------------- План чтения на том (P0) ----------------

    [Fact]
    public void One_HDD_does_not_force_SSD_parallelism_to_one()
    {
        var ssd = new VolumeRecord { Id = 1, MediaType = 4 }; // SSD
        var hdd = new VolumeRecord { Id = 2, MediaType = 3 }; // HDD
        var byId = new Dictionary<long, VolumeRecord> { [1] = ssd, [2] = hdd };

        var plans = VolumeReadPlanner.Build(new long[] { 1, 2 }, byId, requested: 8);

        Assert.Equal(8, plans[1].ContentDop);   // SSD сохраняет высокий DOP
        Assert.False(plans[1].PreferSequential);
        Assert.Equal(1, plans[2].ContentDop);    // HDD читается последовательно
        Assert.True(plans[2].PreferSequential);
    }

    [Fact]
    public void Removable_volume_reads_sequentially()
    {
        var usb = new VolumeRecord { Id = 5, IsRemovable = true };
        var plans = VolumeReadPlanner.Build(new long[] { 5 }, new Dictionary<long, VolumeRecord> { [5] = usb }, 16);
        Assert.Equal(1, plans[5].ContentDop);
    }

    // ---------------- Троттлинг прогресса (P0) ----------------

    [Fact]
    public void Progress_is_throttled_for_many_events()
    {
        int calls = 0;
        using var c = new ProgressCoalescer<int>(_ => Interlocked.Increment(ref calls),
            TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++) c.Report(i);
        sw.Stop();
        c.Flush();

        // ~10 событий/сек + небольшой запас на trailing-таймер и Flush. Точно не миллион.
        long max = sw.ElapsedMilliseconds / 100 + 10;
        Assert.True(calls <= max, $"calls={calls}, max={max}, elapsed={sw.ElapsedMilliseconds}ms");
        Assert.True(calls >= 1, "первое событие должно отдаваться немедленно");
        Assert.True(calls < 1000, $"троттлинг не сработал: {calls} событий");
    }

    [Fact]
    public void Progress_immediate_events_always_emit_and_flush_delivers_last()
    {
        var received = new List<int>();
        var gate = new object();
        using var c = new ProgressCoalescer<int>(v => { lock (gate) received.Add(v); },
            TimeSpan.FromSeconds(30)); // большой интервал: обычные Report не отдаются сами

        c.Report(1, immediate: true);
        c.Report(2, immediate: true);
        c.Report(3, immediate: true);
        c.Report(4); // подавлен троттлингом → ждёт Flush
        c.Flush();

        lock (gate) Assert.Equal(new[] { 1, 2, 3, 4 }, received);
    }

    [Fact]
    public void Progress_immediate_disarms_pending_timer_no_stale_after_terminal()
    {
        var received = new List<int>();
        var gate = new object();
        var c = new ProgressCoalescer<int>(v => { lock (gate) received.Add(v); }, TimeSpan.FromMilliseconds(50));

        c.Report(1);                    // первый — отдаётся сразу
        c.Report(2);                    // в окне троттлинга → взводит trailing-таймер (pending=2)
        c.Report(99, immediate: true);  // «терминал» → снимает таймер и отдаёт 99
        c.Flush();
        c.Dispose();                    // дожидается возможного callback-а
        Thread.Sleep(120);              // снятый таймер не должен выстрелить

        lock (gate) Assert.Equal(new[] { 1, 99 }, received); // 2 подавлено и не всплыло после терминала
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
