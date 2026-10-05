namespace FastDedup.Engine;

/// <summary>
/// Тонкая настройка pipeline на каждый том. Главное отличие от старой логики:
/// один HDD больше не «душит» весь скан, если рядом есть SSD/NVMe.
/// </summary>
public sealed record VolumePlan(
    string Volume,
    StorageType StorageType,
    int StatParallelism,
    int ContentParallelism,
    int ChannelCapacity,
    int HeadTailChunkBytes);

public sealed class ScanPlan
{
    private readonly IReadOnlyDictionary<string, VolumePlan> _plans;

    internal ScanPlan(IReadOnlyDictionary<string, VolumePlan> plans)
    {
        _plans = plans;
    }

    public IReadOnlyDictionary<string, VolumePlan> Volumes => _plans;

    public IReadOnlyDictionary<string, StorageType> VolumeStorage =>
        _plans.ToDictionary(kv => kv.Key, kv => kv.Value.StorageType, StringComparer.OrdinalIgnoreCase);

    public int GlobalStatParallelism => Math.Min(128, Math.Max(1, _plans.Values.Sum(p => p.StatParallelism)));

    public int GlobalChannelCapacity => Math.Min(500_000, Math.Max(10_000, _plans.Values.Sum(p => p.ChannelCapacity)));

    public VolumePlan ForVolume(string volume)
    {
        if (_plans.TryGetValue(volume, out var plan)) return plan;
        return new VolumePlan(volume, StorageType.Unknown,
            Math.Max(1, Environment.ProcessorCount),
            Math.Max(1, Environment.ProcessorCount),
            50_000,
            64 * 1024);
    }
}

internal static class ScanPlanner
{
    public static ScanPlan Create(IReadOnlyList<string> roots, ScanOptions options)
    {
        var storageByVolume = new Dictionary<string, StorageType>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string vol = VolumeOfPath(root);
            if (!storageByVolume.ContainsKey(vol))
            {
                storageByVolume[vol] = options.ForceStorageType
                    ?? (OperatingSystem.IsWindows() ? StorageDetector.Detect(vol) : StorageType.Unknown);
            }
        }

        var plans = new Dictionary<string, VolumePlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var (volume, storage) in storageByVolume)
        {
            int statDop = options.MaxStatParallelism ?? DefaultStatParallelism(storage);
            int contentDop = options.MaxContentParallelism ?? DefaultContentParallelism(storage);
            int channel = options.ChannelCapacity ?? DefaultChannelCapacity(storage);
            int chunk = options.HeadTailChunkBytes > 0 ? options.HeadTailChunkBytes : 64 * 1024;
            plans[volume] = new VolumePlan(volume, storage,
                Math.Max(1, statDop), Math.Max(1, contentDop), Math.Max(1_000, channel), chunk);
        }

        return new ScanPlan(plans);
    }

    public static string VolumeOfPath(string fullPath)
    {
        var rootPath = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (rootPath != null && rootPath.Length >= 2 && rootPath[1] == ':')
            return rootPath.Substring(0, 2);
        return rootPath ?? string.Empty;
    }

    private static int DefaultStatParallelism(StorageType storage) => storage switch
    {
        StorageType.Hdd => Math.Min(Environment.ProcessorCount, 8),
        StorageType.Ssd => Math.Min(Environment.ProcessorCount * 4, 64),
        _ => Math.Max(1, Environment.ProcessorCount)
    };

    private static int DefaultContentParallelism(StorageType storage) => storage switch
    {
        StorageType.Hdd => 2,
        StorageType.Ssd => Math.Min(Environment.ProcessorCount * 4, 64),
        _ => Math.Max(1, Environment.ProcessorCount)
    };

    private static int DefaultChannelCapacity(StorageType storage) => storage switch
    {
        StorageType.Hdd => 25_000,
        StorageType.Ssd => 100_000,
        _ => 50_000
    };
}
