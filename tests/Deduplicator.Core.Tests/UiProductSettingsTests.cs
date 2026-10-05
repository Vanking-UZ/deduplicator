using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

public sealed class UiProductSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dedup_ui_settings_" + Guid.NewGuid().ToString("N"));

    public UiProductSettingsTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Legacy_performance_settings_keep_their_original_meaning()
    {
        var regular = new AppSettings { PerformanceMode = null, EcoMode = false, MaxParallelism = 7 };
        var gentle = new AppSettings { PerformanceMode = null, EcoMode = true, MaxParallelism = 7 };

        Assert.Equal(7, PerformanceProfiles.ResolveParallelism(regular, processorCount: 32));
        Assert.Equal(1, PerformanceProfiles.ResolveParallelism(gentle, processorCount: 32));
    }

    [Fact]
    public void Settings_service_detects_legacy_json_without_performance_mode()
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.SettingsPath, "{\"EcoMode\":false,\"MaxParallelism\":7}");

        var loaded = new SettingsService(paths).Current;

        Assert.Null(loaded.PerformanceMode);
        Assert.Equal(7, PerformanceProfiles.ResolveParallelism(loaded, processorCount: 32));
    }

    [Theory]
    [InlineData(PerformanceProfiles.Automatic, 12, 6)]
    [InlineData(PerformanceProfiles.Gentle, 12, 1)]
    [InlineData(PerformanceProfiles.Maximum, 32, 16)]
    [InlineData(PerformanceProfiles.Maximum, 4, 4)]
    public void Simple_performance_modes_resolve_to_bounded_parallelism(
        string mode, int processorCount, int expected)
    {
        var settings = new AppSettings { PerformanceMode = mode };

        Assert.Equal(expected, PerformanceProfiles.ResolveParallelism(settings, processorCount));
    }

    [Fact]
    public void Expert_parallelism_overrides_simple_mode()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceProfiles.Gentle,
            UseExpertParallelism = true,
            MaxParallelism = 5
        };

        Assert.Equal(5, PerformanceProfiles.ResolveParallelism(settings, processorCount: 16));
    }

    [Fact]
    public void Visual_preferences_and_performance_mode_round_trip_through_settings_service()
    {
        var paths = new AppPaths(_dir);
        var service = new SettingsService(paths);
        service.Save(new AppSettings
        {
            PerformanceMode = PerformanceProfiles.Maximum,
            UseExpertParallelism = true,
            MaxParallelism = 9,
            UseElevatedMftHelper = true,
            UiScale = 1.25,
            ReduceMotion = true
        });

        var loaded = new SettingsService(paths).Current;

        Assert.Equal(PerformanceProfiles.Maximum, loaded.PerformanceMode);
        Assert.True(loaded.UseExpertParallelism);
        Assert.Equal(9, loaded.MaxParallelism);
        Assert.True(loaded.UseElevatedMftHelper);
        Assert.Equal(1.25, loaded.UiScale, 3);
        Assert.True(loaded.ReduceMotion);
    }

    [Fact]
    public void Elevated_mft_helper_is_strictly_opt_in()
        => Assert.False(new AppSettings().UseElevatedMftHelper);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
