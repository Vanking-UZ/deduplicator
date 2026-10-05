namespace Deduplicator.Core.Engine;

/// <summary>Профиль логического тома для чистого планирования по физическим устройствам.</summary>
public readonly record struct VolumeExecutionProfile(
    long VolumeId, string PhysicalDeviceKey, int ContentDop, bool PreferSequential);

/// <summary>
/// Итоговый бюджет одного физического устройства. Несколько разделов одного диска делят общий DOP.
/// </summary>
public sealed record PhysicalDeviceExecutionPlan(
    string PhysicalDeviceKey, IReadOnlyList<long> VolumeIds, int ContentDop, bool PreferSequential);

/// <summary>
/// Запускает работу над набором элементов с параллелизмом НА КАЖДЫЙ ТОМ: разные тома обрабатываются
/// одновременно, но внутри тома — со своим DOP (HDD=1 последовательно, SSD высокий). Глобальный
/// потолок применяется СТАТИЧЕСКИМИ квотами (<see cref="DistributeCap"/>), а не блокирующим
/// семафором: gate.Wait внутри вложенного Parallel.ForEach парковал потоки и раздувал ThreadPool
/// на слабых машинах при мультиномных сканах (этап 1.7 дорожной карты).
/// Единая реализация для exact-воронки и перцептивных этапов.
/// </summary>
public static class VolumeParallelExecutor
{
    public static void RunByVolume<T>(
        IReadOnlyCollection<T> items,
        Func<T, long> volumeKey,
        Func<long, int> contentDopFor,
        int maxVolumeParallelism,
        CancellationToken ct,
        Action<T> body)
        => RunByVolume(items, volumeKey, contentDopFor,
            vid => $"volume:{vid}", _ => false, maxVolumeParallelism, ct, body);

    /// <summary>
    /// Физически осведомлённый запуск. Независимые устройства работают параллельно; разделы одного
    /// устройства делят единый DOP. Если хотя бы один план устройства последовательный, весь девайс
    /// читается строго в один поток — это исключает seek-шторм между разделами HDD.
    /// </summary>
    public static void RunByVolume<T>(
        IReadOnlyCollection<T> items,
        Func<T, long> volumeKey,
        Func<long, int> contentDopFor,
        Func<long, string?> physicalDeviceKeyFor,
        Func<long, bool> preferSequentialFor,
        int maxPhysicalDeviceParallelism,
        CancellationToken ct,
        Action<T> body)
    {
        if (items.Count == 0) return;

        var byVolume = items.GroupBy(volumeKey).ToDictionary(g => g.Key, g => g);
        var profiles = byVolume.Keys.Select(vid => new VolumeExecutionProfile(
            vid,
            physicalDeviceKeyFor(vid) ?? $"volume:{vid}",
            contentDopFor(vid),
            preferSequentialFor(vid))).ToList();
        var devices = GroupPhysicalDevices(profiles);
        var requested = devices.Select(d => (d.PhysicalDeviceKey, Dop: d.ContentDop)).ToList();
        int globalCap = GlobalCap(requested.Select(r => r.Dop).ToList());
        var quotas = DistributeCap(requested, globalCap);

        var outer = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1,
                Math.Min(devices.Count, Math.Min(Math.Max(1, maxPhysicalDeviceParallelism), globalCap)))
        };

        Parallel.ForEach(devices, outer, device =>
        {
            int dop = quotas.TryGetValue(device.PhysicalDeviceKey, out var q) ? q : 1;
            var deviceItems = device.VolumeIds.SelectMany(vid => byVolume[vid]);

            if (device.PreferSequential || dop <= 1)
            {
                foreach (var item in deviceItems)
                {
                    ct.ThrowIfCancellationRequested();
                    body(item);
                }
                return;
            }

            var inner = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = dop };
            Parallel.ForEach(deviceItems, inner, body);
        });
    }

    /// <summary>
    /// Чистая группировка томов по физическому ключу. У SSD/NVMe разделы делят минимальный из
    /// запрошенных DOP (динамический governor одного раздела тем самым сжимает весь девайс), а HDD
    /// получает DOP=1 независимо от числа разделов.
    /// </summary>
    public static List<PhysicalDeviceExecutionPlan> GroupPhysicalDevices(
        IEnumerable<VolumeExecutionProfile> volumes)
    {
        var normalized = volumes
            .GroupBy(v => v.VolumeId)
            .Select(g => g.First())
            .Select(v => string.IsNullOrWhiteSpace(v.PhysicalDeviceKey)
                ? v with { PhysicalDeviceKey = $"volume:{v.VolumeId}" }
                : v)
            .ToList();

        var result = new List<PhysicalDeviceExecutionPlan>();
        foreach (var device in normalized.GroupBy(v => v.PhysicalDeviceKey, StringComparer.OrdinalIgnoreCase))
        {
            var members = device.ToList();
            bool sequential = members.Any(v => v.PreferSequential);
            int dop = sequential ? 1 : members.Min(v => Math.Max(1, v.ContentDop));
            result.Add(new PhysicalDeviceExecutionPlan(device.Key,
                members.Select(v => v.VolumeId).ToList(), dop, sequential));
        }
        return result;
    }

    /// <summary>
    /// Глобальный потолок одновременных чтений содержимого по всем томам. Базово — СУММА DOP томов
    /// (каждый том читает на своей глубине), но ограничен сверху 2× числом ядер, чтобы множество томов
    /// не вызывало переподписку; при этом не ниже DOP самого «толстого» тома. Чистая функция — тестируема.
    /// </summary>
    public static int GlobalCap(IReadOnlyCollection<int> perVolumeDops)
    {
        if (perVolumeDops.Count == 0) return 1;
        int maxSingle = perVolumeDops.Max(d => Math.Max(1, d));
        int sum = perVolumeDops.Sum(d => Math.Max(1, d));
        return Math.Max(1, Math.Min(sum, Math.Max(maxSingle, Environment.ProcessorCount * 2)));
    }

    /// <summary>
    /// Распределить глобальный потолок между томами статическими квотами (без блокировок):
    /// каждая квота ≥ 1, суммарно вписываемся в cap, урезая самые «толстые» тома первыми.
    /// Чистая функция — тестируема. Спроектирована так, чтобы губернатор ресурсов (этап 2.6)
    /// мог динамически сжимать cap между пачками работы.
    /// </summary>
    public static Dictionary<TKey, int> DistributeCap<TKey>(
        IReadOnlyList<(TKey Key, int Dop)> volumes, int cap) where TKey : notnull
    {
        var result = new Dictionary<TKey, int>(volumes.Count);
        foreach (var (key, dop) in volumes) result[key] = Math.Max(1, dop);

        int total = result.Values.Sum();
        while (total > Math.Max(cap, volumes.Count)) // ниже 1 на том не ужимаем
        {
            TKey topKey = default!;
            int topVal = 1;
            foreach (var kv in result)
                if (kv.Value > topVal) { topKey = kv.Key; topVal = kv.Value; }
            if (topVal <= 1) break;
            result[topKey] = topVal - 1;
            total--;
        }
        return result;
    }
}
