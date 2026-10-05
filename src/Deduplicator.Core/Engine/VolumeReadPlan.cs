using Deduplicator.Core.Models;

namespace Deduplicator.Core.Engine;

/// <summary>
/// План чтения для одного тома. Главное отличие от старой глобальной логики: один HDD больше не
/// «душит» весь скан — SSD/NVMe получают свой высокий DOP, а HDD/съёмные читаются последовательно.
/// </summary>
public sealed record VolumeReadPlan(
    long VolumeId,
    string PhysicalDeviceKey,
    string Label,
    bool PreferSequential,
    int MetadataDop,
    int ContentDop);

/// <summary>Строит план чтения по типу носителя. Чистая функция — легко тестируется.</summary>
public static class VolumeReadPlanner
{
    /// <summary>
    /// DOP содержимого: HDD/съёмные — 1 (последовательно, NFR-7), SSD/неизвестно — запрошенный.
    /// DOP метаданных дешевле: на HDD держим умеренный параллелизм (≤2), а не сериализуем в 1.
    /// </summary>
    public static VolumeReadPlan ForVolume(VolumeRecord? v, long volumeId, int requested)
    {
        int req = Math.Max(1, requested);
        bool sequential = v?.PreferSequentialReads ?? false;
        int contentDop = sequential ? 1 : req;
        int metaDop = sequential ? Math.Min(req, 2) : req;
        string physicalKey = string.IsNullOrWhiteSpace(v?.PhysicalDeviceKey)
            ? $"volume:{volumeId}"
            : v.PhysicalDeviceKey!;
        return new VolumeReadPlan(volumeId, physicalKey, v?.DisplayName ?? $"#{volumeId}",
            sequential, metaDop, contentDop);
    }

    public static Dictionary<long, VolumeReadPlan> Build(
        IEnumerable<long> volumeIds, IReadOnlyDictionary<long, VolumeRecord> byId, int requested)
    {
        var plans = new Dictionary<long, VolumeReadPlan>();
        foreach (var id in volumeIds.Distinct())
        {
            byId.TryGetValue(id, out var v);
            plans[id] = ForVolume(v, id, requested);
        }
        return plans;
    }
}
