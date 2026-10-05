using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace FastDedup.Engine;

/// <summary>
/// Сердце движка. Запускает многоступенчатую воронку:
///   1) перечисление (MFT/USN или fallback) -> 2) metadata + size bucket ->
///   3) хэш головы/хвоста -> 4) полный хэш -> 5) побайтовая сверка -> группы дубликатов.
///
/// Улучшения этой версии:
///   * план на каждый том: SSD/NVMe и HDD получают разные DOP, один HDD не тормозит весь скан;
///   * lazy size-buckets без промежуточного ConcurrentQueue всех файлов;
///   * проверка стабильности размера между metadata и hash-stage;
///   * hardlink-алиасы схлопываются, чтобы не завышать освобождаемое место;
///   * bounded channel оставляет backpressure и не даёт перечислению разогнать память.
/// </summary>
public sealed class DedupOrchestrator
{
    public async Task<ScanResult> ScanAsync(
        ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = new ConcurrentBag<ScanError>();
        long bytesRead = 0;
        long filesChangedSkipped = 0;
        long hardLinkAliasesSkipped = 0;

        var roots = options.Roots.Select(Path.GetFullPath).ToList();
        var plan = ScanPlanner.Create(roots, options);

        // Глобальная константа head/tail-chunk (параметр хэша, а не носителя): нужна, чтобы
        // решение «мелкий файл уже покрыт head/tail» (этап 5) было однозначным для всех томов.
        int chunk = options.HeadTailChunkBytes > 0 ? options.HeadTailChunkBytes : 64 * 1024;
        // Предкомпилированные фильтры — один раз, без аллокаций на каждый файл metadata-этапа.
        var filters = new CompiledScanFilters(options);

        // --- 1. Перечисление -> ограниченный канал путей ---
        var pathChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(plan.GlobalChannelCapacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var enumTask = Task.Run(async () =>
        {
            try
            {
                foreach (var root in roots)
                {
                    ct.ThrowIfCancellationRequested();
                    IFileEnumerator enumerator = SelectEnumerator(root, errors);
                    foreach (var path in enumerator.EnumerateFiles(root, options, errors, ct))
                        await pathChannel.Writer.WriteAsync(path, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* нормальная отмена */ }
            catch (Exception ex) { errors.Add(new ScanError("(enumeration)", ex.Message)); }
            finally { pathChannel.Writer.Complete(); }
        }, ct);

        // --- 2. Metadata + lazy группировка по размеру ---
        var sizeBuckets = new SizeBucketCollector();
        long seen = 0;
        int statDop = plan.GlobalStatParallelism;
        var statWorkers = Enumerable.Range(0, statDop).Select(_ => Task.Run(async () =>
        {
            await foreach (var path in pathChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (!Native.TryGetFileInfo(path, out long size, out uint attrs, out long ctime))
                        continue;
                    if (filters.ShouldSkip(path, size, attrs))
                        continue;

                    sizeBuckets.Add(new FileEntry(path, size, ctime));
                    long n = Interlocked.Increment(ref seen);
                    if ((n & 0xFFFF) == 0) progress?.Report(new ScanProgress(ScanPhase.Enumerating, n));
                }
                catch (Exception ex) { errors.Add(new ScanError(path, ex.Message)); }
            }
        }, ct)).ToArray();

        await enumTask.ConfigureAwait(false);
        await Task.WhenAll(statWorkers).ConfigureAwait(false);
        long filesSeen = Interlocked.Read(ref seen);

        // --- 3. Фильтр по размеру (без чтения с диска) ---
        var sizeGroups = sizeBuckets.GetDuplicateGroups();
        long afterSize = sizeGroups.Sum(l => (long)l.Count);
        progress?.Report(new ScanProgress(ScanPhase.SizeFiltered, afterSize));

        // --- 4. Хэш головы/хвоста ---
        var htInput = sizeGroups.SelectMany(l => l).ToList();
        var htResult = await HashStageByVolumeAsync(
            htInput,
            plan,
            options,
            ScanPhase.HeadTailHashing,
            (f, _, c) => ContentHasher.HeadTailHashAsync(
                f.Path, f.Size, chunk, c, options.VerifyStableSize),
            errors,
            progress,
            reportMask: 0x3FFF,
            ct).ConfigureAwait(false);

        Interlocked.Add(ref bytesRead, htResult.BytesRead);
        Interlocked.Add(ref filesChangedSkipped, htResult.FilesChangedSkipped);

        // Survivors head/tail — bucket-и с ≥2 кандидатами.
        long afterHt = htResult.Buckets.Values.Where(b => b.Count >= 2).Sum(b => (long)b.Count);

        // --- 5. Полный хэш ---
        // Мелкие файлы (size ≤ 2*chunk) на этапе head/tail уже прочитаны ЦЕЛИКОМ, поэтому их
        // head/tail-хэш == полному хэшу содержимого (одинаковый size в bucket-е → одна ветка).
        // Их не перечитываем: кладём прямо в fullBuckets. Полный хэш считаем только крупным.
        var fullBuckets = new ConcurrentDictionary<(long Size, ulong Hash), ConcurrentBag<FileEntry>>();
        var needFull = new List<FileEntry>();
        foreach (var kv in htResult.Buckets)
        {
            if (kv.Value.Count < 2) continue;
            if (kv.Key.Size <= 2L * chunk)
                fullBuckets[kv.Key] = kv.Value;            // head/tail = полный хэш → сразу к byte-compare
            else
                foreach (var f in kv.Value) needFull.Add(f);
        }

        var fullResult = await HashStageByVolumeAsync(
            needFull,
            plan,
            options,
            ScanPhase.FullHashing,
            (f, _, c) => ContentHasher.FullHashAsync(f.Path, f.Size, c, options.VerifyStableSize),
            errors,
            progress,
            reportMask: 0x0FFF,
            ct).ConfigureAwait(false);

        Interlocked.Add(ref bytesRead, fullResult.BytesRead);
        Interlocked.Add(ref filesChangedSkipped, fullResult.FilesChangedSkipped);

        // Слить полный хэш крупных файлов в fullBuckets. Ключи (size,hash) не пересекаются с мелкими:
        // у мелких size ≤ 2*chunk, у крупных size > 2*chunk — один и тот же size не может быть в обоих.
        foreach (var kv in fullResult.Buckets)
        {
            var bag = fullBuckets.GetOrAdd(kv.Key, _ => new ConcurrentBag<FileEntry>());
            foreach (var f in kv.Value) bag.Add(f);
        }

        var fullGroups = fullBuckets.Values.Where(b => b.Count >= 2).Select(b => b.ToList()).ToList();
        long fullHashed = needFull.Count;

        // --- 6. Побайтовая сверка -> финальные группы ---
        progress?.Report(new ScanProgress(ScanPhase.ByteCompare, fullGroups.Count));
        var groups = new List<DuplicateGroup>();
        foreach (var candidate in fullGroups)
        {
            ct.ThrowIfCancellationRequested();
            var compareResult = await ClusterByBytesAsync(candidate, options, errors, ct).ConfigureAwait(false);
            hardLinkAliasesSkipped += compareResult.HardLinkAliasesSkipped;
            foreach (var cluster in compareResult.Clusters)
                if (cluster.Count >= 2)
                    groups.Add(DuplicateGroup.From(cluster, cluster[0].Size));
        }

        sw.Stop();
        var stats = new ScanStats(filesSeen, afterSize, afterHt, fullHashed, bytesRead, sw.Elapsed,
            groups.Sum(g => g.ReclaimableBytes), hardLinkAliasesSkipped, filesChangedSkipped);
        progress?.Report(new ScanProgress(ScanPhase.Done, filesSeen));

        return new ScanResult(groups, errors.ToList(), stats, plan.VolumeStorage);
    }

    private sealed record HashStageResult(
        ConcurrentDictionary<(long Size, ulong Hash), ConcurrentBag<FileEntry>> Buckets,
        long BytesRead,
        long FilesChangedSkipped);

    private static async Task<HashStageResult> HashStageByVolumeAsync(
        IReadOnlyCollection<FileEntry> input,
        ScanPlan plan,
        ScanOptions options,
        ScanPhase phase,
        Func<FileEntry, VolumePlan, CancellationToken, Task<(ulong hash, long bytesRead)>> hash,
        ConcurrentBag<ScanError> errors,
        IProgress<ScanProgress>? progress,
        int reportMask,
        CancellationToken ct)
    {
        var buckets = new ConcurrentDictionary<(long Size, ulong Hash), ConcurrentBag<FileEntry>>();
        long bytesRead = 0;
        long changedSkipped = 0;
        long done = 0;

        var byVolume = input.GroupBy(f => ScanPlanner.VolumeOfPath(f.Path)).ToList();
        int volumeParallelism = options.MaxVolumeParallelism
            ?? Math.Max(1, Math.Min(byVolume.Count, Math.Max(1, Environment.ProcessorCount / 2)));

        await Parallel.ForEachAsync(byVolume,
            new ParallelOptions { MaxDegreeOfParallelism = volumeParallelism, CancellationToken = ct },
            async (volumeGroup, volumeCt) =>
            {
                var volumePlan = plan.ForVolume(volumeGroup.Key);
                var files = volumeGroup.ToList();

                // HDD: минимизируем random seek. SSD/NVMe: порядок не так важен, важна глубина очереди.
                if (volumePlan.StorageType == StorageType.Hdd)
                    files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

                await Parallel.ForEachAsync(files,
                    new ParallelOptions { MaxDegreeOfParallelism = volumePlan.ContentParallelism, CancellationToken = volumeCt },
                    async (f, fileCt) =>
                    {
                        try
                        {
                            var (h, read) = await hash(f, volumePlan, fileCt).ConfigureAwait(false);
                            Interlocked.Add(ref bytesRead, read);
                            buckets.GetOrAdd((f.Size, h), _ => new ConcurrentBag<FileEntry>()).Add(f);
                        }
                        catch (FileChangedDuringScanException ex)
                        {
                            Interlocked.Increment(ref changedSkipped);
                            errors.Add(new ScanError(f.Path, "changed: " + ex.Message));
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new ScanError(f.Path, ex.Message));
                        }

                        long n = Interlocked.Increment(ref done);
                        if ((n & reportMask) == 0) progress?.Report(new ScanProgress(phase, n));
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return new HashStageResult(buckets, bytesRead, changedSkipped);
    }

    private sealed record ByteClusterResult(List<List<FileEntry>> Clusters, long HardLinkAliasesSkipped);

    /// <summary>
    /// Разбивает группу с одинаковым (размер, полный хэш) на кластеры по реальному равенству байтов.
    /// Защита от коллизий хэша перед удалением. После этого, по умолчанию, схлопывает hardlink-алиасы.
    /// </summary>
    private static async Task<ByteClusterResult> ClusterByBytesAsync(
        List<FileEntry> files, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
    {
        var clusters = new List<List<FileEntry>>();
        foreach (var f in files)
        {
            bool placed = false;
            foreach (var cluster in clusters)
            {
                try
                {
                    if (await ContentHasher.FilesEqualAsync(cluster[0].Path, f.Path, ct).ConfigureAwait(false))
                    {
                        cluster.Add(f);
                        placed = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // Не смогли сверить — безопаснее НЕ считать дубликатом.
                    errors.Add(new ScanError(f.Path, "compare: " + ex.Message));
                    placed = true;
                    break;
                }
            }
            if (!placed) clusters.Add(new List<FileEntry> { f });
        }

        if (options.IncludeHardLinkAliases)
            return new ByteClusterResult(clusters, 0);

        long skipped = 0;
        var reducedClusters = new List<List<FileEntry>>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var reduced = CollapseHardLinkAliases(cluster, ref skipped);
            if (reduced.Count >= 2) reducedClusters.Add(reduced);
        }

        return new ByteClusterResult(reducedClusters, skipped);
    }

    private static List<FileEntry> CollapseHardLinkAliases(List<FileEntry> cluster, ref long skipped)
    {
        if (!OperatingSystem.IsWindows() || cluster.Count < 2) return cluster;

        var seen = new HashSet<(uint VolumeSerial, ulong FileIndex)>();
        var reduced = new List<FileEntry>(cluster.Count);
        foreach (var f in cluster)
        {
            if (Native.TryGetFileIdentity(f.Path, out var id))
            {
                var key = (id.VolumeSerialNumber, id.FileIndex);
                if (!seen.Add(key))
                {
                    skipped++;
                    continue;
                }
            }
            reduced.Add(f);
        }
        return reduced;
    }

    private IFileEnumerator SelectEnumerator(string root, ConcurrentBag<ScanError> errors)
    {
        try
        {
            string vol = ScanPlanner.VolumeOfPath(root);
            if (OperatingSystem.IsWindows() && vol.Length == 2 && vol[1] == ':')
            {
                var di = new DriveInfo(vol + "\\");
                bool ntfs = di.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
                if (Native.IsElevated() && ntfs)
                    return new UsnEnumerator(); // быстрый путь
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ScanError(root, "выбор перечислителя: " + ex.Message));
        }
        return new FallbackEnumerator();
    }
}
