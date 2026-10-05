using System.Runtime.InteropServices;
using System.Text;

namespace Deduplicator.Core.Engine;

/// <summary>Ожидаемая identity локального тома на время одного скана.</summary>
public sealed record VolumeIdentityExpectation(long VolumeId, string MountPath, string Serial);

/// <summary>
/// Носитель исчез или под той же буквой появился другой том. Это фатальная ошибка скана: продолжение
/// смешало бы метаданные/хэши разных физических носителей под одним volume_id.
/// </summary>
public sealed class VolumeIdentityChangedException : IOException
{
    public long VolumeId { get; }
    public string MountPath { get; }
    public string ExpectedSerial { get; }
    public string? ActualSerial { get; }

    public VolumeIdentityChangedException(long volumeId, string mountPath, string expectedSerial,
        string? actualSerial)
        : base(actualSerial is null
            ? $"Носитель {mountPath} отключён или недоступен во время сканирования. Результаты не опубликованы."
            : $"Под буквой {mountPath} появился другой носитель (ожидался {expectedSerial}, сейчас {actualSerial}). " +
              "Сканирование остановлено, результаты не опубликованы.")
    {
        VolumeId = volumeId;
        MountPath = mountPath;
        ExpectedSerial = expectedSerial;
        ActualSerial = actualSerial;
    }
}

/// <summary>Нативное чтение серийного номера тома без использования кэшированного DriveInfo.</summary>
public static class VolumeIdentityProbe
{
    public static string? TryGetSerial(string mountPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(mountPath)) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(mountPath));
            if (string.IsNullOrWhiteSpace(root)) return null;
            var volName = new StringBuilder(261);
            var fsName = new StringBuilder(261);
            if (!GetVolumeInformation(root, volName, volName.Capacity, out uint serial,
                    out _, out _, fsName, fsName.Capacity))
                return null;
            return $"{(serial >> 16) & 0xFFFF:X4}-{serial & 0xFFFF:X4}";
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);
}

/// <summary>
/// Сторож identity с дешёвым троттлингом для горячих циклов и принудительной проверкой перед каждой
/// пакетной записью/на границе фаз. Класс не зависит от WinAPI: probe и часы инъецируются в тестах.
/// </summary>
public sealed class VolumeIdentityGuard
{
    private sealed class Entry
    {
        public required VolumeIdentityExpectation Expected { get; init; }
        public readonly object Sync = new();
        public TimeSpan LastChecked;
        public bool Checked;
    }

    private readonly Dictionary<long, Entry> _entries;
    private readonly Func<string, string?> _probe;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _hotLoopInterval;

    public VolumeIdentityGuard(IEnumerable<VolumeIdentityExpectation> expectations,
        Func<string, string?>? probe = null, Func<TimeSpan>? clock = null,
        TimeSpan? hotLoopInterval = null)
    {
        _entries = expectations
            .GroupBy(e => e.VolumeId)
            .ToDictionary(g => g.Key, g => new Entry { Expected = g.First() });
        _probe = probe ?? VolumeIdentityProbe.TryGetSerial;
        _clock = clock ?? (() => TimeSpan.FromSeconds(
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency));
        _hotLoopInterval = hotLoopInterval ?? TimeSpan.FromSeconds(1);
    }

    public static VolumeIdentityGuard Capture(
        IEnumerable<(long VolumeId, string MountPath)> volumes,
        Func<string, string?>? probe = null)
    {
        probe ??= VolumeIdentityProbe.TryGetSerial;
        var expectations = new List<VolumeIdentityExpectation>();
        foreach (var (volumeId, mountPath) in volumes.DistinctBy(v => v.VolumeId))
        {
            var serial = probe(mountPath);
            if (serial is null)
                throw new IOException($"Не удалось подтвердить серийный номер носителя {mountPath}. " +
                                      "Проверка не запущена, чтобы не смешать данные разных дисков.");
            expectations.Add(new VolumeIdentityExpectation(volumeId, mountPath, serial));
        }
        return new VolumeIdentityGuard(expectations, probe);
    }

    public void VerifyVolume(long volumeId, bool force = false)
    {
        if (!_entries.TryGetValue(volumeId, out var entry)) return;
        var now = _clock();
        lock (entry.Sync)
        {
            if (!force && entry.Checked && now - entry.LastChecked < _hotLoopInterval) return;
            var actual = _probe(entry.Expected.MountPath);
            if (!string.Equals(actual, entry.Expected.Serial, StringComparison.OrdinalIgnoreCase))
                throw new VolumeIdentityChangedException(entry.Expected.VolumeId,
                    entry.Expected.MountPath, entry.Expected.Serial, actual);
            entry.LastChecked = now;
            entry.Checked = true;
        }
    }

    public void VerifyAll(bool force = false)
    {
        foreach (var id in _entries.Keys) VerifyVolume(id, force);
    }
}
