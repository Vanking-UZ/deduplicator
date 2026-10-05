using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public sealed class ScanProgressAndIdentityTests
{
    [Fact]
    public void Unknown_file_total_does_not_invent_enumeration_percent_or_eta()
    {
        var now = TimeSpan.Zero;
        var estimator = new ScanProgressEstimator(ScanDepth.ExactOnly, clock: () => now);

        estimator.BeginPhase(ScanProgressPhase.Enumerating, 0, 0, 0);
        now = TimeSpan.FromSeconds(20);
        var estimate = estimator.Update(filesSeen: 10_000, filesProcessed: 10_000,
            bytesProcessed: 0);

        Assert.Null(estimate.OverallFraction);
        Assert.Null(estimate.EstimatedTotalFiles);
        Assert.Null(estimate.EstimatedRemaining);
    }

    [Fact]
    public void Overall_progress_is_monotonic_across_phase_fraction_resets()
    {
        var now = TimeSpan.Zero;
        var estimator = new ScanProgressEstimator(ScanDepth.WithSimilarImages,
            estimatedTotalFiles: 100, clock: () => now);

        estimator.BeginPhase(ScanProgressPhase.Enumerating, 0, 0, 0);
        now += TimeSpan.FromSeconds(3);
        var enumeration = estimator.Update(50, 50, 0);
        var enumerationDone = estimator.CompletePhase(100, 100, 0);

        estimator.BeginPhase(ScanProgressPhase.Exact, 100, 100, 0);
        now += TimeSpan.FromSeconds(2);
        var exactHigh = estimator.Update(100, 140, 10_000, 0.80);
        now += TimeSpan.FromSeconds(1);
        var exactRegressed = estimator.Update(100, 150, 12_000, 0.20);

        Assert.Null(enumeration.OverallFraction);
        Assert.NotNull(enumerationDone.OverallFraction);
        Assert.True(exactHigh.OverallFraction >= enumerationDone.OverallFraction);
        Assert.Equal(exactHigh.OverallFraction!.Value, exactRegressed.OverallFraction!.Value, precision: 10);

        var finished = estimator.Finish(true, 100, 150, 12_000);
        Assert.Equal(1, finished.OverallFraction);
        Assert.Null(finished.EstimatedRemaining);
    }

    [Fact]
    public void Pause_is_excluded_from_elapsed_rates_and_eta()
    {
        var now = TimeSpan.Zero;
        var estimator = new ScanProgressEstimator(ScanDepth.ExactOnly,
            estimatedTotalFiles: 100, clock: () => now);
        estimator.BeginPhase(ScanProgressPhase.Enumerating, 0, 0, 0);
        estimator.CompletePhase(0, 0, 0);
        estimator.BeginPhase(ScanProgressPhase.Exact, 0, 0, 0);

        now += TimeSpan.FromSeconds(1);
        _ = estimator.Update(10, 10, 1_000);
        now += TimeSpan.FromSeconds(1);
        var beforePause = estimator.Update(20, 20, 3_000);
        Assert.True(beforePause.FilesPerSecond > 0);
        Assert.True(beforePause.BytesPerSecond > 0);
        Assert.NotNull(beforePause.EstimatedRemaining);

        estimator.Pause();
        now += TimeSpan.FromMinutes(10);
        var paused = estimator.Update(20, 20, 3_000);
        Assert.Equal(beforePause.Elapsed, paused.Elapsed);
        Assert.Null(paused.EstimatedRemaining);

        estimator.Resume();
        var resumed = estimator.Update(20, 20, 3_000); // новая rate-база, без 10 минут простоя
        now += TimeSpan.FromSeconds(1);
        var afterResume = estimator.Update(30, 30, 5_000);

        Assert.Equal(beforePause.Elapsed, resumed.Elapsed);
        Assert.Equal(beforePause.Elapsed + TimeSpan.FromSeconds(1), afterResume.Elapsed);
        Assert.True(afterResume.FilesPerSecond >= 9.9);
        Assert.NotNull(afterResume.EstimatedRemaining);
    }

    [Fact]
    public void Historical_file_estimate_only_grows_when_seen_exceeds_it()
    {
        var now = TimeSpan.Zero;
        var estimator = new ScanProgressEstimator(ScanDepth.ExactOnly,
            estimatedTotalFiles: 100, clock: () => now);
        estimator.BeginPhase(ScanProgressPhase.Enumerating, 0, 0, 0);

        now += TimeSpan.FromSeconds(1);
        var estimate = estimator.Update(120, 120, 0);

        Assert.NotNull(estimate.EstimatedTotalFiles);
        Assert.True(estimate.EstimatedTotalFiles >= 120);
        Assert.Null(estimate.OverallFraction);
    }

    [Fact]
    public void Heartbeat_without_work_does_not_reset_throughput_window()
    {
        var now = TimeSpan.Zero;
        var estimator = new ScanProgressEstimator(ScanDepth.ExactOnly, clock: () => now);
        estimator.BeginPhase(ScanProgressPhase.Exact, 0, 0, 0);

        now = TimeSpan.FromSeconds(1);
        var first = estimator.Update(0, 1, 100, 0.1);
        Assert.Equal(100, first.BytesPerSecond, precision: 6);

        now = TimeSpan.FromSeconds(2);
        _ = estimator.Update(0, 1, 100, 0.1); // heartbeat
        now = TimeSpan.FromSeconds(3);
        _ = estimator.Update(0, 1, 100, 0.1); // heartbeat
        now = TimeSpan.FromSeconds(5);
        var afterLongRead = estimator.Update(0, 2, 500, 0.2);

        Assert.Equal(100, afterLongRead.BytesPerSecond, precision: 6);
    }

    [Fact]
    public void Identity_guard_throws_when_letter_now_points_to_another_serial()
    {
        var now = TimeSpan.Zero;
        string? actual = "AAAA-BBBB";
        int probes = 0;
        var guard = new VolumeIdentityGuard(
            new[] { new VolumeIdentityExpectation(7, @"X:\", "AAAA-BBBB") },
            _ => { probes++; return actual; }, () => now, TimeSpan.FromSeconds(1));

        guard.VerifyVolume(7, force: true);
        actual = "CCCC-DDDD";

        // Горячий путь троттлится, но пакетная/фазовая force-проверка обязана увидеть swap сразу.
        guard.VerifyVolume(7);
        Assert.Equal(1, probes);
        var ex = Assert.Throws<VolumeIdentityChangedException>(() => guard.VerifyVolume(7, force: true));
        Assert.Equal("AAAA-BBBB", ex.ExpectedSerial);
        Assert.Equal("CCCC-DDDD", ex.ActualSerial);
    }

    [Fact]
    public void Identity_guard_treats_unreadable_volume_as_fatal()
    {
        var guard = new VolumeIdentityGuard(
            new[] { new VolumeIdentityExpectation(9, @"Y:\", "1111-2222") },
            _ => null);

        var ex = Assert.Throws<VolumeIdentityChangedException>(() => guard.VerifyAll(force: true));
        Assert.Null(ex.ActualSerial);
    }

    [Theory]
    [InlineData(100_000, false, 100_000)]
    [InlineData(200_000, false, 131_072)]
    [InlineData(200_000, true, 200_000)]
    [InlineData(200_000_000, false, 196_608)]
    public void Partial_byte_estimate_matches_hash_read_plan(long size, bool sequential, long expected)
        => Assert.Equal(expected, HashService.EstimatePartialBytes(size, sequential));
}
