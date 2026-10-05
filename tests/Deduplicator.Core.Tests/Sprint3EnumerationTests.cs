using FastDedup.Engine;

namespace Deduplicator.Core.Tests;

/// <summary>
/// MVP: матрица бэкендов перечисления. Главное правило — для НЕ-NTFS НЕ предлагать запуск от
/// администратора (это не создаст MFT на exFAT/FAT32), и для каждого диска свой подходящий бэкенд.
/// </summary>
public class Sprint3EnumerationTests
{
    [Fact]
    public void NonNtfs_Admin_DoesNotSuggestAdmin()
    {
        var d = BackendSelector.Decide(@"F:\", @"F:\", "exFAT", DriveType.Removable,
            isWindows: true, isElevated: true, isWholeVolume: true);
        Assert.Equal(EnumerationBackend.Win32Find, d.Backend);
        Assert.Equal(BackendFallbackReason.NonNtfs, d.Reason);

        var msg = BackendMessages.BuildUserNotice(d);
        Assert.DoesNotContain("администратора", msg); // ГЛАВНОЕ: для exFAT не зовём админа
        Assert.Contains("exFAT", msg);
    }

    [Fact]
    public void Ntfs_NotAdmin_SuggestsNarrowHelper_NotWholeAppElevation()
    {
        var d = BackendSelector.Decide(@"G:\", @"G:\", "NTFS", DriveType.Fixed,
            isWindows: true, isElevated: false, isWholeVolume: true);
        Assert.Equal(EnumerationBackend.Win32Find, d.Backend);
        Assert.Equal(BackendFallbackReason.NotElevated, d.Reason);
        var message = BackendMessages.BuildUserNotice(d);
        Assert.Contains("MFT-помощник", message);
        Assert.Contains("повышать права всего приложения не нужно", message);
        Assert.DoesNotContain("запустите программу от имени администратора", message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ntfs_Admin_WholeVolume_UsesMft()
    {
        var d = BackendSelector.Decide(@"D:\", @"D:\", "NTFS", DriveType.Fixed,
            isWindows: true, isElevated: true, isWholeVolume: true);
        Assert.Equal(EnumerationBackend.NtfsMft, d.Backend);
        Assert.Equal(BackendFallbackReason.None, d.Reason);
        Assert.DoesNotContain("администратора", BackendMessages.BuildUserNotice(d));
    }

    [Fact]
    public void Ntfs_Admin_Subfolder_UsesWin32FindByDefault()
    {
        var d = BackendSelector.Decide(@"C:\Users\Name\Pictures", @"C:\", "NTFS", DriveType.Fixed,
            isWindows: true, isElevated: true, isWholeVolume: false);
        Assert.Equal(EnumerationBackend.Win32Find, d.Backend);
        Assert.Equal(BackendFallbackReason.NotWholeVolume, d.Reason);
    }

    [Fact]
    public void NetworkPath_UsesWin32FindNetwork()
    {
        var d = BackendSelector.Decide(@"\\NAS\Photos", @"\\NAS\Photos", null, DriveType.Network,
            isWindows: true, isElevated: true, isWholeVolume: false);
        Assert.Equal(EnumerationBackend.Win32FindNetwork, d.Backend);
        var msg = BackendMessages.BuildUserNotice(d);
        Assert.DoesNotContain("администратора", msg);
        Assert.Contains("Сетевой", msg);
    }

    [Fact]
    public void Unc_path_detected_even_without_drivetype()
    {
        var d = BackendSelector.Decide(@"\\server\share\sub", null, null, null,
            isWindows: true, isElevated: true, isWholeVolume: false);
        Assert.Equal(EnumerationBackend.Win32FindNetwork, d.Backend);
    }

    [Fact]
    public void NonWindows_UsesWin32Find()
    {
        var d = BackendSelector.Decide("/mnt/data", null, "ext4", null,
            isWindows: false, isElevated: true, isWholeVolume: true);
        Assert.Equal(EnumerationBackend.Win32Find, d.Backend);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Users\Name\Pictures", false)]
    [InlineData(@"\\server\share", false)]
    public void IsWholeVolumeRoot_detects_volume_root_vs_subfolder(string path, bool expected)
    {
        Assert.Equal(expected, BackendSelector.IsWholeVolumeRoot(path));
    }
}
