using System.Runtime.InteropServices;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

/// <summary>Регрессионные тесты на исправления из код-ревью.</summary>
public class HardeningTests : IDisposable
{
    private readonly string _dir;

    public HardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_hard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Settings_load_clamps_out_of_range_thresholds()
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.SettingsPath,
            "{\"DefaultImageThreshold\":5.0,\"DefaultAudioThreshold\":-1.0,\"MaxParallelism\":999}");

        var svc = new SettingsService(paths);

        Assert.InRange(svc.Current.DefaultImageThreshold, 0.5, 1.0);
        Assert.InRange(svc.Current.DefaultAudioThreshold, 0.5, 1.0);
        Assert.InRange(svc.Current.MaxParallelism, 1, 64);
    }

    [Fact]
    public void Settings_corrupt_file_falls_back_to_defaults()
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.SettingsPath, "{ this is not valid json ");
        var svc = new SettingsService(paths);
        Assert.Equal(0.90, svc.Current.DefaultImageThreshold, 3);
    }

    [Fact]
    public void Oversized_settings_are_rejected_without_large_json_materialization()
    {
        var paths = new AppPaths(_dir);
        using (var stream = new FileStream(paths.SettingsPath, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(1024 * 1024 + 1);
        }

        var svc = new SettingsService(paths);

        Assert.Equal(0.90, svc.Current.DefaultImageThreshold, 3);
    }

    [Fact]
    public void Settings_save_is_atomic_and_leaves_no_temporary_file()
    {
        var paths = new AppPaths(_dir);
        var svc = new SettingsService(paths);

        svc.Current.Theme = "Dark";
        svc.Save(svc.Current);

        Assert.Contains("Dark", File.ReadAllText(paths.SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*" + SafeIo.TempSuffix));
    }

    [Fact]
    public void LongPath_prefixes_drive_and_unc_paths_on_windows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        Assert.Equal(@"\\?\C:\a\b", LongPath.Fix(@"C:\a\b"));
        Assert.Equal(@"\\?\UNC\server\share\f", LongPath.Fix(@"\\server\share\f"));
        // Уже префиксованный путь не трогаем.
        Assert.Equal(@"\\?\C:\x", LongPath.Fix(@"\\?\C:\x"));
        // Относительный путь — без изменений.
        Assert.Equal(@"rel\path", LongPath.Fix(@"rel\path"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
