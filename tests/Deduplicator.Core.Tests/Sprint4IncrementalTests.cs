using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Решение об инкрементальности по состоянию USN-журнала + хранение состояния тома.
/// (Нативное чтение журнала требует админ+NTFS и здесь не проверяется; покрыта чистая логика выбора.)
/// </summary>
public class Sprint4IncrementalTests : IDisposable
{
    private const int Ver = IncrementalScanPlanner.EngineVersion;
    private readonly string _dir;

    public Sprint4IncrementalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private static VolumeScanState Saved(long journal, long nextUsn, string optHash = "OPT") => new()
    {
        VolumeId = 1, JournalId = journal, NextUsn = nextUsn, EngineVersion = Ver, OptionsHash = optHash
    };

    [Fact]
    public void Journal_unavailable_forces_full_rescan()
    {
        var d = IncrementalScanPlanner.Decide(Saved(10, 500), journalAvailable: false,
            0, 0, 0, Ver, "OPT");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void First_scan_with_no_saved_state_is_full_rescan()
    {
        var d = IncrementalScanPlanner.Decide(null, journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 0, currentNextUsn: 500, Ver, "OPT");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void No_change_second_scan_is_unchanged_and_skips_enumeration()
    {
        // Тот же журнал и тот же NextUsn → том не менялся → перечисление можно пропустить.
        var d = IncrementalScanPlanner.Decide(Saved(10, 500), journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 0, currentNextUsn: 500, Ver, "OPT");
        Assert.Equal(ScanPlanKind.Unchanged, d.Kind);
    }

    [Fact]
    public void Advanced_usn_means_incremental()
    {
        var d = IncrementalScanPlanner.Decide(Saved(10, 500), journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 0, currentNextUsn: 800, Ver, "OPT");
        Assert.Equal(ScanPlanKind.Incremental, d.Kind);
    }

    [Fact]
    public void Journal_recreated_forces_full_rescan()
    {
        var d = IncrementalScanPlanner.Decide(Saved(10, 500), journalAvailable: true,
            currentJournalId: 99, currentFirstUsn: 0, currentNextUsn: 800, Ver, "OPT");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void Journal_trimmed_past_saved_usn_forces_full_rescan()
    {
        // FirstUsn уехал за сохранённый NextUsn → часть изменений потеряна → полный пересбор.
        var d = IncrementalScanPlanner.Decide(Saved(10, 500), journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 600, currentNextUsn: 900, Ver, "OPT");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void Changed_scan_options_force_full_rescan()
    {
        var d = IncrementalScanPlanner.Decide(Saved(10, 500, "OLD"), journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 0, currentNextUsn: 500, Ver, "NEW");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void Changed_engine_version_forces_full_rescan()
    {
        var stale = Saved(10, 500);
        stale.EngineVersion = Ver - 1;
        var d = IncrementalScanPlanner.Decide(stale, journalAvailable: true,
            currentJournalId: 10, currentFirstUsn: 0, currentNextUsn: 500, Ver, "OPT");
        Assert.Equal(ScanPlanKind.FullRescan, d.Kind);
    }

    [Fact]
    public void VolumeScanState_repository_roundtrips()
    {
        var db = new Database(Path.Combine(_dir, "state.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var repo = new VolumeScanStateRepository(db);

        var vol = new VolumeRecord { Serial = "ST", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        Assert.Null(repo.Get(vol.Id));

        repo.Upsert(new VolumeScanState
        {
            VolumeId = vol.Id, JournalId = 12345, NextUsn = 67890,
            IndexedAt = DateTimeOffset.Now, EngineVersion = Ver, OptionsHash = "HASH"
        });

        var got = repo.Get(vol.Id);
        Assert.NotNull(got);
        Assert.Equal(12345, got!.JournalId);
        Assert.Equal(67890, got.NextUsn);
        Assert.Equal("HASH", got.OptionsHash);

        // Upsert обновляет ту же строку.
        repo.Upsert(new VolumeScanState { VolumeId = vol.Id, JournalId = 12345, NextUsn = 99999, EngineVersion = Ver, OptionsHash = "HASH" });
        Assert.Equal(99999, repo.Get(vol.Id)!.NextUsn);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
