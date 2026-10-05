using System.Buffers.Binary;
using System.Text;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Models;
using Fde = FastDedup.Engine;

namespace Deduplicator.Core.Tests;

/// <summary>
/// USN-дельта-реконсайл. Живой журнал недоступен без прав администратора
/// (тесты идут unelevated), поэтому проверяются все НЕнативные слои:
///  (а) чистая классификация записей дельты (UsnDeltaPlanner.Classify/Build) — все Reason-комбинации;
///  (б) применение дельты (UsnDeltaExecutor.Apply) к реальной тестовой БД + temp-файлам —
///      «прочитать дельту» отделено от «применить дельту» хуками, журнал не нужен;
///  (в) разбор буфера записей журнала (UsnDeltaReader.TryAppendRecords) на буферах, собранных
///      руками: смещения V2, кап 100k, версия ≠ V2, запись без имени, граница StopUsn.
/// Сам FSCTL_READ_USN_JOURNAL и OpenFileById остаются непроверенными вживую (нужен админ+NTFS);
/// они guarded: любой сбой → «дельта недоступна» → полный пересбор.
/// </summary>
public class Sprint19UsnDeltaTests : IDisposable
{
    private readonly string _dir;

    public Sprint19UsnDeltaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s19_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------------- (а) Классификация: файловые записи ----------------

    [Theory]
    [InlineData(UsnReason.FileDelete)]
    [InlineData(UsnReason.FileDelete | UsnReason.Close)]
    [InlineData(UsnReason.RenameOldName)]
    [InlineData(UsnReason.RenameOldName | UsnReason.Close)]
    // Создан и удалён в одной записи (маски накапливаются до Close) — имени на диске больше нет.
    [InlineData(UsnReason.FileCreate | UsnReason.FileDelete | UsnReason.Close)]
    // Переименован сюда и удалён — строку по ЭТОМУ (новому) пути тоже удалить.
    [InlineData(UsnReason.RenameNewName | UsnReason.FileDelete | UsnReason.Close)]
    public void Classify_file_delete_and_rename_old_are_delete_row(uint reason)
        => Assert.Equal(UsnDeltaAction.DeleteRow, UsnDeltaPlanner.Classify(reason, isDirectory: false));

    [Theory]
    [InlineData(UsnReason.FileCreate)]
    [InlineData(UsnReason.FileCreate | UsnReason.Close)]
    [InlineData(UsnReason.RenameNewName)]
    [InlineData(UsnReason.DataOverwrite)]
    [InlineData(UsnReason.DataExtend)]
    [InlineData(UsnReason.DataTruncation)]
    [InlineData(UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.Close)]
    [InlineData(UsnReason.NamedDataOverwrite)]
    [InlineData(UsnReason.NamedDataExtend)]
    [InlineData(UsnReason.NamedDataTruncation)]
    [InlineData(UsnReason.BasicInfoChange)]
    [InlineData(UsnReason.EaChange)]
    [InlineData(UsnReason.SecurityChange)]
    [InlineData(UsnReason.HardLinkChange)]
    [InlineData(UsnReason.CompressionChange)]
    [InlineData(UsnReason.EncryptionChange)]
    [InlineData(UsnReason.ObjectIdChange)]
    [InlineData(UsnReason.ReparsePointChange)]
    [InlineData(UsnReason.StreamChange)]
    [InlineData(UsnReason.IndexableChange)]
    // Чистый Close без других флагов — консервативно пере-stat: лишний stat дёшев и не теряет изменение.
    [InlineData(UsnReason.Close)]
    public void Classify_file_modifications_are_restat(uint reason)
        => Assert.Equal(UsnDeltaAction.Restat, UsnDeltaPlanner.Classify(reason, isDirectory: false));

    [Theory]
    [InlineData(UsnReason.DataOverwrite)]
    [InlineData(UsnReason.DataExtend | UsnReason.Close)]
    [InlineData(UsnReason.NamedDataTruncation)]
    [InlineData(UsnReason.StreamChange)]
    [InlineData(UsnReason.HardLinkChange)]
    [InlineData(UsnReason.FileCreate)]
    [InlineData(UsnReason.RenameNewName | UsnReason.Close)]
    [InlineData(UsnReason.CompressionChange)]
    [InlineData(UsnReason.EncryptionChange)]
    [InlineData(UsnReason.ReparsePointChange)]
    [InlineData(0u)]                    // отсутствующая причина — неоднозначность
    [InlineData(0x00400000u)]           // неизвестный движку бит — консервативный сброс
    public void Hash_invalidation_policy_rejects_content_and_ambiguous_reasons(uint reason)
        => Assert.True(UsnDeltaPlanner.RequiresHashInvalidation(reason));

    [Theory]
    [InlineData(UsnReason.Close)]
    [InlineData(UsnReason.BasicInfoChange | UsnReason.Close)]
    [InlineData(UsnReason.SecurityChange)]
    [InlineData(UsnReason.EaChange | UsnReason.IndexableChange | UsnReason.ObjectIdChange)]
    public void Hash_invalidation_policy_allows_only_known_metadata_reasons(uint reason)
        => Assert.False(UsnDeltaPlanner.RequiresHashInvalidation(reason));

    // ---------------- (а) Классификация: каталожные записи ----------------

    [Theory]
    [InlineData(UsnReason.FileDelete)]
    [InlineData(UsnReason.RenameOldName)]
    [InlineData(UsnReason.RenameNewName)]
    [InlineData(UsnReason.RenameOldName | UsnReason.Close)]
    [InlineData(UsnReason.FileDelete | UsnReason.Close)]
    public void Classify_directory_rename_or_delete_is_anomaly(uint reason)
        => Assert.Equal(UsnDeltaAction.DirectoryAnomaly, UsnDeltaPlanner.Classify(reason, isDirectory: true));

    [Theory]
    [InlineData(UsnReason.FileCreate)]
    [InlineData(UsnReason.BasicInfoChange)]
    [InlineData(UsnReason.SecurityChange | UsnReason.Close)]
    public void Classify_directory_create_or_metadata_change_is_ignored(uint reason)
        => Assert.Equal(UsnDeltaAction.Ignore, UsnDeltaPlanner.Classify(reason, isDirectory: true));

    // ---------------- (а) Сборка плана: слияние по пути, аномалии ----------------

    [Fact]
    public void Build_groups_by_path_and_restat_wins_regardless_of_order()
    {
        // Удалён → создан заново: судьбу решает диск (restat), а не порядок записей.
        var p = Path.Combine(_dir, "a.txt");
        var plan1 = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(p, UsnReason.FileDelete | UsnReason.Close, false),
            new Fde.UsnDeltaChange(p, UsnReason.FileCreate | UsnReason.Close, false)
        });
        Assert.Null(plan1.AnomalyReason);
        Assert.Empty(plan1.Deletes);
        Assert.Equal(new[] { p }, plan1.Restats.Select(r => r.FullPath));
        Assert.Equal(UsnReason.FileDelete | UsnReason.FileCreate | UsnReason.Close,
            Assert.Single(plan1.Restats).Reason);

        // Обратный порядок (создан → удалён) даёт ТОТ ЖЕ план: stat промахнётся и строка удалится.
        var plan2 = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(p, UsnReason.FileCreate | UsnReason.Close, false),
            new Fde.UsnDeltaChange(p, UsnReason.FileDelete | UsnReason.Close, false)
        });
        Assert.Null(plan2.AnomalyReason);
        Assert.Empty(plan2.Deletes);
        Assert.Equal(new[] { p }, plan2.Restats.Select(r => r.FullPath));
        Assert.Equal(UsnReason.FileDelete | UsnReason.FileCreate | UsnReason.Close,
            Assert.Single(plan2.Restats).Reason);
    }

    [Fact]
    public void Build_pure_delete_goes_to_deletes_and_rename_pair_splits()
    {
        var gone = Path.Combine(_dir, "gone.txt");
        var oldP = Path.Combine(_dir, "old.txt");
        var newP = Path.Combine(_dir, "new.txt");
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(gone, UsnReason.FileDelete | UsnReason.Close, false),
            new Fde.UsnDeltaChange(oldP, UsnReason.RenameOldName, false),
            new Fde.UsnDeltaChange(newP, UsnReason.RenameNewName | UsnReason.Close, false)
        });
        Assert.Null(plan.AnomalyReason);
        Assert.Equal(new[] { gone, oldP }.OrderBy(x => x), plan.Deletes.OrderBy(x => x));
        Assert.Equal(new[] { newP }, plan.Restats.Select(r => r.FullPath));
    }

    [Fact]
    public void Build_directory_rename_or_delete_is_full_rescan_signal()
    {
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(Path.Combine(_dir, "file.txt"), UsnReason.DataOverwrite, false),
            new Fde.UsnDeltaChange(Path.Combine(_dir, "photos"), UsnReason.RenameOldName, true)
        });
        Assert.NotNull(plan.AnomalyReason);
        Assert.Empty(plan.Deletes);
        Assert.Empty(plan.Restats);
    }

    [Fact]
    public void Build_ignores_directory_creation_but_keeps_file_changes()
    {
        var f = Path.Combine(_dir, "f.txt");
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(Path.Combine(_dir, "newdir"), UsnReason.FileCreate | UsnReason.Close, true),
            new Fde.UsnDeltaChange(f, UsnReason.FileCreate | UsnReason.Close, false)
        });
        Assert.Null(plan.AnomalyReason);
        Assert.Equal(new[] { f }, plan.Restats.Select(r => r.FullPath));
    }

    [Fact]
    public void Build_empty_path_is_anomaly()
    {
        var plan = UsnDeltaPlanner.Build(new[] { new Fde.UsnDeltaChange("", UsnReason.FileCreate, false) });
        Assert.NotNull(plan.AnomalyReason);
    }

    [Fact]
    public void Build_case_only_rename_applies_both_delete_and_restat()
    {
        // Смена только регистра: rel_path в БД сравнивается байтово — нужны ОБА действия.
        var oldP = Path.Combine(_dir, "photo.JPG");
        var newP = Path.Combine(_dir, "PHOTO.jpg");
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(oldP, UsnReason.RenameOldName, false),
            new Fde.UsnDeltaChange(newP, UsnReason.RenameNewName | UsnReason.Close, false)
        });
        Assert.Null(plan.AnomalyReason);
        Assert.Equal(new[] { oldP }, plan.Deletes);
        Assert.Equal(new[] { newP }, plan.Restats.Select(r => r.FullPath));
    }

    // ---------------- (в) Разбор буфера журнала (без FSCTL) ----------------

    /// <summary>Собрать буфер FSCTL_READ_USN_JOURNAL руками: 8 байт «USN продолжения» + записи V2.</summary>
    private static byte[] BuildJournalBuffer(long nextCursor,
        params (ushort Major, ulong Frn, ulong ParentFrn, long Usn, uint Reason, uint Attrs, string Name)[] recs)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(nextCursor);
        foreach (var r in recs)
        {
            var name = Encoding.Unicode.GetBytes(r.Name);
            int recLen = (60 + name.Length + 7) & ~7; // выравнивание записей по 8 байт
            long start = ms.Position;
            w.Write(recLen);            // RecordLength
            w.Write(r.Major);           // MajorVersion
            w.Write((ushort)0);         // MinorVersion
            w.Write(r.Frn);             // FileReferenceNumber
            w.Write(r.ParentFrn);       // ParentFileReferenceNumber
            w.Write(r.Usn);             // Usn
            w.Write(0L);                // TimeStamp
            w.Write(r.Reason);          // Reason
            w.Write(0u);                // SourceInfo
            w.Write(0u);                // SecurityId
            w.Write(r.Attrs);           // FileAttributes
            w.Write((ushort)name.Length); // FileNameLength
            w.Write((ushort)60);        // FileNameOffset
            w.Write(name);
            while (ms.Position < start + recLen) w.Write((byte)0); // паддинг до RecordLength
        }
        return ms.ToArray();
    }

    [Fact]
    public void TryAppendRecords_parses_v2_records_and_returns_cursor()
    {
        var buf = BuildJournalBuffer(nextCursor: 500,
            (2, 0x10ul, 0x5ul, 100, UsnReason.DataOverwrite | UsnReason.Close, 0x20, "doc.txt"),
            (2, 0x11ul, 0x5ul, 200, UsnReason.FileDelete | UsnReason.Close, 0x20, "старый файл.jpg"));

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(buf, buf.Length, stopUsn: 1000, maxRecords: 100,
            records, out long next, out string? anomaly);

        Assert.True(ok);
        Assert.Null(anomaly);
        Assert.Equal(500, next);
        Assert.Equal(2, records.Count);
        Assert.Equal("doc.txt", records[0].FileName);
        Assert.Equal(0x10ul, records[0].Frn);
        Assert.Equal(0x5ul, records[0].ParentFrn);
        Assert.Equal(100, records[0].Usn);
        Assert.Equal(UsnReason.DataOverwrite | UsnReason.Close, records[0].Reason);
        Assert.Equal("старый файл.jpg", records[1].FileName); // Unicode-имя не искажается
        Assert.Equal(UsnReason.FileDelete | UsnReason.Close, records[1].Reason);
    }

    [Fact]
    public void TryAppendRecords_rejects_truncated_tail_instead_of_silent_partial_success()
    {
        var complete = BuildJournalBuffer(nextCursor: 500,
            (2, 0x10ul, 0x5ul, 100, UsnReason.DataOverwrite, 0x20, "doc.txt"),
            (2, 0x11ul, 0x5ul, 200, UsnReason.DataOverwrite, 0x20, "second.txt"));
        var truncated = complete[..^3];

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(truncated, truncated.Length, 1000, 100,
            records, out _, out string? anomaly);

        Assert.False(ok);
        Assert.Contains("неполная", anomaly);
    }

    [Fact]
    public void TryAppendRecords_skips_records_at_or_beyond_stop_usn()
    {
        // Записи выше водяного знака StopUsn не входят в дельту — их дочитает следующий скан.
        var buf = BuildJournalBuffer(nextCursor: 900,
            (2, 1ul, 5ul, 100, UsnReason.DataOverwrite, 0x20, "in.txt"),
            (2, 2ul, 5ul, 300, UsnReason.DataOverwrite, 0x20, "at-stop.txt"),
            (2, 3ul, 5ul, 400, UsnReason.DataOverwrite, 0x20, "beyond.txt"));

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(buf, buf.Length, stopUsn: 300, maxRecords: 100,
            records, out _, out _);

        Assert.True(ok);
        Assert.Equal(new[] { "in.txt" }, records.Select(r => r.FileName));
    }

    [Fact]
    public void TryAppendRecords_cap_exceeded_makes_delta_unavailable()
    {
        var buf = BuildJournalBuffer(nextCursor: 900,
            (2, 1ul, 5ul, 100, UsnReason.DataOverwrite, 0x20, "a.txt"),
            (2, 2ul, 5ul, 110, UsnReason.DataOverwrite, 0x20, "b.txt"),
            (2, 3ul, 5ul, 120, UsnReason.DataOverwrite, 0x20, "c.txt"));

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(buf, buf.Length, stopUsn: 1000, maxRecords: 2,
            records, out _, out string? anomaly);

        Assert.False(ok);
        Assert.Contains("полный пересбор", anomaly);
    }

    [Fact]
    public void TryAppendRecords_v3_record_makes_delta_unavailable()
    {
        var buf = BuildJournalBuffer(nextCursor: 900,
            (3, 1ul, 5ul, 100, UsnReason.DataOverwrite, 0x20, "a.txt"));

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(buf, buf.Length, stopUsn: 1000, maxRecords: 100,
            records, out _, out string? anomaly);

        Assert.False(ok);
        Assert.Contains("V3", anomaly);
    }

    [Fact]
    public void TryAppendRecords_nameless_record_makes_delta_unavailable()
    {
        var buf = BuildJournalBuffer(nextCursor: 900,
            (2, 1ul, 5ul, 100, UsnReason.DataOverwrite, 0x20, ""));

        var records = new List<Fde.UsnDeltaRecord>();
        bool ok = Fde.UsnDeltaReader.TryAppendRecords(buf, buf.Length, stopUsn: 1000, maxRecords: 100,
            records, out _, out string? anomaly);

        Assert.False(ok);
        Assert.Contains("без имени", anomaly);
    }

    // ---------------- (б) Применение дельты: реальная БД + temp-файлы ----------------

    private sealed record ApplyCtx(FileRepository Files, long VolumeId, string MountRoot)
    {
        public string Rel(string name) => Path.GetRelativePath(MountRoot, Path.Combine(MountRoot, name));
        public FileRecord? Row(string name) => Files.GetByVolumeAndPath(VolumeId, Rel(name));
    }

    private ApplyCtx BuildDb(string name)
    {
        var mount = Path.Combine(_dir, name);
        Directory.CreateDirectory(mount);
        var db = new Database(Path.Combine(_dir, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var vol = new VolumeRecord { Serial = "S19-" + name, MountPath = mount, IsConnected = true };
        vol.Id = volumes.Upsert(vol);
        return new ApplyCtx(new FileRepository(db), vol.Id, mount);
    }

    /// <summary>Проиндексировать файл в тестовую БД как продукт (метаданные с диска, без хэшей).</summary>
    private static FileRecord IndexFromDisk(ApplyCtx ctx, string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var record = new FileRecord
        {
            VolumeId = ctx.VolumeId,
            RelPath = Path.GetRelativePath(ctx.MountRoot, fullPath),
            Name = fi.Name,
            Ext = fi.Extension,
            Size = fi.Length,
            Mtime = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
            Created = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero),
            Category = FileCategory.Other,
            ScanState = ScanState.Meta,
            LastIndexed = DateTimeOffset.Now
        };
        ctx.Files.UpsertMetaBatch(new[] { record });
        return record;
    }

    /// <summary>Хуки применения «как в продукте»: stat с диска, индексирование через UpsertMetaBatch.</summary>
    private static UsnDeltaApplyHooks RealHooks(ApplyCtx ctx, Func<Fde.EnumeratedFile, bool>? filter = null)
        => new()
        {
            Stat = p => Fde.FileStatReader.TryGet(p),
            TryIndexFile = (ef, _) =>
            {
                if (filter is not null && !filter(ef)) return false;
                IndexFromDisk(ctx, ef.FullPath);
                return true;
            },
            DeleteByRelPath = rel => ctx.Files.DeleteByVolumeAndPath(ctx.VolumeId, rel),
            VolumeAlive = () => Directory.Exists(ctx.MountRoot)
        };

    [Fact]
    public void Apply_reindexes_changed_file_and_resets_hashes_and_verified()
    {
        var ctx = BuildDb("changed");
        var full = Path.Combine(ctx.MountRoot, "doc.txt");
        File.WriteAllText(full, "старое содержимое");
        var row = IndexFromDisk(ctx, full);

        // Проставляем хэши и кэш побайтовой сверки — они ОБЯЗАНЫ сброситься при переиндексации.
        var hash = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        row.PartialHash = hash;
        row.FullHash = hash;
        row.ScanState = ScanState.Full;
        ctx.Files.UpdateComputedBatch(new[] { row });
        ctx.Files.UpdateVerifiedFullHashBatch(new[] { (row.Id, hash) });
        Assert.NotNull(ctx.Row("doc.txt")!.VerifiedFullHash);

        // Файл изменился на диске (размер и mtime «в будущее», чтобы отличаться наверняка).
        File.WriteAllText(full, "новое, заметно более длинное содержимое файла");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(5));
        long newSize = new FileInfo(full).Length;

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.Close, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Reindexed);
        Assert.Equal(0, outcome.Deleted);

        var updated = ctx.Row("doc.txt");
        Assert.NotNull(updated);
        Assert.Equal(newSize, updated!.Size);                      // размер обновился
        Assert.Equal(new FileInfo(full).LastWriteTimeUtc, updated.Mtime.UtcDateTime); // mtime обновился
        Assert.Null(updated.FullHash);                             // хэши сброшены (перехэш в воронке)
        Assert.Null(updated.PartialHash);
        Assert.Null(updated.VerifiedFullHash);                     // кэш побайтовой сверки сброшен
    }

    [Fact]
    public void Apply_data_change_with_same_metadata_preserves_reason_and_forces_hash_reset()
    {
        var ctx = BuildDb("same_meta_changed");
        var full = Path.Combine(ctx.MountRoot, "same-size.txt");
        File.WriteAllText(full, "AAAA");
        var row = IndexFromDisk(ctx, full);

        var hash = Enumerable.Repeat((byte)0xCD, 16).ToArray();
        row.PartialHash = hash;
        row.FullHash = hash;
        row.ScanState = ScanState.Full;
        ctx.Files.UpdateComputedBatch(new[] { row });
        ctx.Files.UpdateVerifiedFullHashBatch(new[] { (row.Id, hash) });

        // Переписываем содержимое тем же числом байтов и возвращаем mtime. Обычная строгая
        // metadata-сверка сочла бы файл неизменившимся, но USN DataOverwrite сильнее метаданных.
        var originalMtime = File.GetLastWriteTimeUtc(full);
        File.WriteAllText(full, "BBBB");
        File.SetLastWriteTimeUtc(full, originalMtime);
        Assert.Equal(row.Size, new FileInfo(full).Length);

        const uint reason = UsnReason.DataOverwrite | UsnReason.Close;
        uint observedReason = 0;
        var plan = UsnDeltaPlanner.Build(new[] { new Fde.UsnDeltaChange(full, reason, false) });
        var hooks = new UsnDeltaApplyHooks
        {
            Stat = p => Fde.FileStatReader.TryGet(p),
            TryIndexFile = (ef, changeReason) =>
            {
                observedReason = changeReason;
                if (!UsnDeltaPlanner.RequiresHashInvalidation(changeReason)) return true;
                IndexFromDisk(ctx, ef.FullPath); // production-путь: UpsertMetaBatch сбрасывает хэши
                return true;
            },
            DeleteByRelPath = rel => ctx.Files.DeleteByVolumeAndPath(ctx.VolumeId, rel),
            VolumeAlive = () => true
        };

        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, hooks);

        Assert.True(outcome.Success);
        Assert.Equal(reason, observedReason);
        var updated = ctx.Row("same-size.txt");
        Assert.NotNull(updated);
        Assert.Null(updated!.PartialHash);
        Assert.Null(updated.FullHash);
        Assert.Null(updated.VerifiedFullHash);
    }

    [Fact]
    public void Apply_deletes_row_for_deleted_file()
    {
        var ctx = BuildDb("deleted");
        var full = Path.Combine(ctx.MountRoot, "gone.txt");
        File.WriteAllText(full, "x");
        IndexFromDisk(ctx, full);
        File.Delete(full);

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.FileDelete | UsnReason.Close, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Deleted);
        Assert.Null(ctx.Row("gone.txt")); // строка удалена из files
    }

    [Fact]
    public void Apply_rename_removes_old_row_and_creates_new()
    {
        var ctx = BuildDb("renamed");
        var oldFull = Path.Combine(ctx.MountRoot, "old-name.txt");
        var newFull = Path.Combine(ctx.MountRoot, "new-name.txt");
        File.WriteAllText(oldFull, "переименуй меня");
        IndexFromDisk(ctx, oldFull);
        File.Move(oldFull, newFull);

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(oldFull, UsnReason.RenameOldName, false),
            new Fde.UsnDeltaChange(newFull, UsnReason.RenameNewName | UsnReason.Close, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));

        Assert.True(outcome.Success);
        Assert.Null(ctx.Row("old-name.txt"));    // старая строка удалена
        var moved = ctx.Row("new-name.txt");     // новая появилась
        Assert.NotNull(moved);
        Assert.Equal("new-name.txt", moved!.Name);
    }

    [Fact]
    public void Apply_restat_of_missing_file_deletes_row()
    {
        // Создан и тут же удалён между сканами (или запись Restat, а файла уже нет): stat промах →
        // строку (если была) удалить. Диск — источник истины.
        var ctx = BuildDb("missing");
        var full = Path.Combine(ctx.MountRoot, "flash.txt");
        File.WriteAllText(full, "y");
        IndexFromDisk(ctx, full);
        File.Delete(full);

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.FileCreate | UsnReason.DataOverwrite | UsnReason.Close, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Deleted);
        Assert.Null(ctx.Row("flash.txt"));
    }

    [Fact]
    public void Apply_filtered_out_file_gets_row_deleted()
    {
        // Файл существует, но отфильтрован (например, стал скрытым при SkipHiddenSystem или
        // уехал в $Recycle.Bin): держать строку со старыми хэшами при изменённом содержимом
        // опасно — в полном скане с прунингом её бы тоже не осталось.
        var ctx = BuildDb("filtered");
        var full = Path.Combine(ctx.MountRoot, "hidden.txt");
        File.WriteAllText(full, "z");
        IndexFromDisk(ctx, full);

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.BasicInfoChange | UsnReason.Close, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx, filter: _ => false));

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Reindexed);
        Assert.Null(ctx.Row("hidden.txt"));
    }

    [Fact]
    public void Apply_directory_anomaly_changes_nothing()
    {
        var ctx = BuildDb("dir_anomaly");
        var full = Path.Combine(ctx.MountRoot, "keep.txt");
        File.WriteAllText(full, "не трогать");
        IndexFromDisk(ctx, full);

        // Каталожная rename-запись → сигнал FullRescan уже на сборке плана.
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.FileDelete | UsnReason.Close, false),
            new Fde.UsnDeltaChange(Path.Combine(ctx.MountRoot, "sub"), UsnReason.RenameOldName, true)
        });
        Assert.NotNull(plan.AnomalyReason);

        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));
        Assert.False(outcome.Success);
        Assert.NotNull(ctx.Row("keep.txt")); // ничего не применялось, строка цела
    }

    [Fact]
    public void Apply_path_outside_volume_is_anomaly_and_nothing_is_applied()
    {
        var ctx = BuildDb("outside");
        var full = Path.Combine(ctx.MountRoot, "safe.txt");
        File.WriteAllText(full, "внутри тома");
        IndexFromDisk(ctx, full);

        // Валидация путей идёт ДО каких-либо изменений БД: чужой путь отменяет ВСЮ дельту,
        // включая валидные удаления из того же плана.
        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.FileDelete | UsnReason.Close, false),
            new Fde.UsnDeltaChange(Path.Combine(_dir, "чужой.txt"), UsnReason.DataOverwrite, false)
        });
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, RealHooks(ctx));

        Assert.False(outcome.Success);
        Assert.Contains("вне тома", outcome.AnomalyReason);
        Assert.NotNull(ctx.Row("safe.txt")); // удаление не применилось — план отброшен целиком
    }

    [Fact]
    public void Apply_dead_volume_is_anomaly_not_mass_delete()
    {
        // Носитель выдернули: stat промахивается по ВСЕМ путям, но это не «файлы удалены» —
        // строки индекса не выкашиваются, дельта отменяется.
        var ctx = BuildDb("dead");
        var full = Path.Combine(ctx.MountRoot, "photo.jpg");
        File.WriteAllText(full, "фото");
        IndexFromDisk(ctx, full);

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.DataOverwrite | UsnReason.Close, false)
        });
        var hooks = new UsnDeltaApplyHooks
        {
            Stat = _ => null,                 // «файла нет»
            TryIndexFile = (_, _) => true,
            DeleteByRelPath = rel => ctx.Files.DeleteByVolumeAndPath(ctx.VolumeId, rel),
            VolumeAlive = () => false         // …потому что тома больше нет
        };
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, hooks);

        Assert.False(outcome.Success);
        Assert.NotNull(ctx.Row("photo.jpg")); // строка цела — деградация безопасна
    }

    [Fact]
    public void Apply_exception_midway_becomes_anomaly_for_full_rescan()
    {
        var ctx = BuildDb("faulty");
        var full = Path.Combine(ctx.MountRoot, "boom.txt");
        File.WriteAllText(full, "q");

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.DataOverwrite | UsnReason.Close, false)
        });
        var hooks = new UsnDeltaApplyHooks
        {
            Stat = p => Fde.FileStatReader.TryGet(p),
            TryIndexFile = (_, _) => throw new IOException("диск закапризничал"),
            DeleteByRelPath = _ => { },
            VolumeAlive = () => true
        };
        var outcome = UsnDeltaExecutor.Apply(plan, ctx.MountRoot, hooks);

        Assert.False(outcome.Success); // аномалия → вызывающий уйдёт в полный пересбор
        Assert.Contains("сбой применения", outcome.AnomalyReason);
    }

    [Fact]
    public void Apply_cancellation_propagates_and_is_not_swallowed_as_anomaly()
    {
        var ctx = BuildDb("cancel");
        var full = Path.Combine(ctx.MountRoot, "c.txt");
        File.WriteAllText(full, "c");

        var plan = UsnDeltaPlanner.Build(new[]
        {
            new Fde.UsnDeltaChange(full, UsnReason.DataOverwrite | UsnReason.Close, false)
        });
        var hooks = new UsnDeltaApplyHooks
        {
            Stat = p => Fde.FileStatReader.TryGet(p),
            TryIndexFile = (_, _) => throw new OperationCanceledException(),
            DeleteByRelPath = _ => { },
            VolumeAlive = () => true
        };
        Assert.Throws<OperationCanceledException>(() => UsnDeltaExecutor.Apply(plan, ctx.MountRoot, hooks));
    }
}
