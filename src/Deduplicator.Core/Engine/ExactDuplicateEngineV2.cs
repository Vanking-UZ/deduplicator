using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Engine;

/// <summary>
/// Потоковая воронка точных дубликатов:
///   потоковые size-кандидаты (один запрос, без N+1) → частичный хэш → полный хэш только для
///   коллизий частичного → точные группы с обязательной побайтовой сверкой подключённых файлов.
///
/// Метаданные уже должны быть в БД (этап перечисления в <c>ScanService</c>). Движок только читает
/// содержимое там, где есть шанс на дубль: уникальные размеры не читаются вовсе.
/// </summary>
public sealed class ExactDuplicateEngineV2 : IExactDuplicateEngine
{
    private readonly FileRepository _files;
    private readonly IHashService _hash;
    private readonly IDuplicateGrouper _grouper;

    public ExactDuplicateEngineV2(FileRepository files, IHashService hash, IDuplicateGrouper grouper)
    {
        _files = files;
        _hash = hash;
        _grouper = grouper;
    }

    // Сколько размеров-кандидатов выбирать одним запросом. Не N+1 (на каждый размер), но и не
    // один курсор на весь скан (тот держал бы WAL read-mark открытым и раздувал WAL на гигабайты).
    private const int SizeBatch = 200;
    private const int MaxFilesPerSizeBatch = 20_000;
    private const int LargeBucketPage = 2_000;

    public ExactScanResult ScanExact(ExactScanRequest req, IExactScanReporter rep, CancellationToken ct)
    {
        var result = new ExactScanResult();
        rep.Stage(ScanStage.PartialHashing);

        // Кандидаты считаются по явной области сессии (выбранные диски/папки), а не по всей истории БД.
        long total = _files.CountFilesWithDuplicateSizes(req.SessionId);
        long processed = 0;

        // Размеры, встречающиеся >1 раза, — одним запросом (список long, мало памяти). Обрабатываем
        // ПАЧКАМИ размеров: короткоживущая выборка `GetBySizesOrdered` закрывается ДО записи хэшей,
        // поэтому read-mark не удерживает WAL от чекпоинта. Память ограничена одной пачкой размеров.
        var sizeCandidates = _files.GetDuplicateSizeCandidateCounts(req.SessionId);
        for (int i = 0; i < sizeCandidates.Count;)
        {
            rep.WaitGate();
            ct.ThrowIfCancellationRequested();

            var candidate = sizeCandidates[i];
            if (candidate.Count > MaxFilesPerSizeBatch)
            {
                ProcessLargeSizeBucket(candidate.Size, candidate.Count, req, rep, ct, result,
                    ref processed, total);
                i++;
                _files.PassiveCheckpoint();
                continue;
            }

            var batch = new List<long>(SizeBatch);
            long batchFiles = 0;
            while (i < sizeCandidates.Count && batch.Count < SizeBatch &&
                   sizeCandidates[i].Count <= MaxFilesPerSizeBatch &&
                   (batch.Count == 0 || batchFiles + sizeCandidates[i].Count <= MaxFilesPerSizeBatch))
            {
                batch.Add(sizeCandidates[i].Size);
                batchFiles += sizeCandidates[i].Count;
                i++;
            }

            // 2.1: корзины пачки материализуются целиком (GroupBy буферизует выборку, reader закрыт
            // до записи хэшей), а хэширование идёт одним вызовом на ФАЗУ всей пачки — см. ProcessSizeBatch.
            var buckets = _files.GetBySizesOrdered(batch, req.SessionId)
                .GroupBy(f => f.Size).Select(g => g.ToList()).ToList();
            ProcessSizeBatch(buckets, req, rep, ct, result, ref processed, total);

            _files.PassiveCheckpoint(); // reader закрыт → SQLite может освободить WAL
        }

        // Точные группы (включая кросс-том/офлайн hash-only) — безопасная побайтовая сверка внутри.
        // waitGate: сверка читает файлы целиком (часто самый долгий этап) — пауза обязана работать
        // и здесь, а не только в size-батчах и хэш-воронке.
        rep.VerifyVolumes();
        rep.Stage(ScanStage.Grouping);
        result.GroupsCreated = _grouper.BuildExactGroups(req.VolumesById, req.InScope, req.Rule, rep.ExactGroups,
            ct, req.SessionId, rep.WaitGate, req.IsPreferredKeep);
        return result;
    }

    /// <summary>
    /// Отдельный ограниченный путь для огромной корзины одного размера. Типичный пример — сотни
    /// тысяч нулевых/фиксированных блоков. Ни исходная корзина, ни partial-коллизия целиком в RAM
    /// не попадают: обе фазы идут keyset-страницами, коллизии отбирает SQLite.
    /// </summary>
    private void ProcessLargeSizeBucket(long size, long bucketCount, ExactScanRequest req,
        IExactScanReporter rep, CancellationToken ct, ExactScanResult result,
        ref long processed, long total)
    {
        result.SizeCandidates += bucketCount;

        long afterId = 0, partialVisited = 0;
        while (true)
        {
            rep.WaitGate();
            ct.ThrowIfCancellationRequested();
            var page = _files.GetBySizePage(size, afterId, LargeBucketPage, req.SessionId);
            if (page.Count == 0) break;
            afterId = page[^1].Id;
            partialVisited += page.Count;

            var needPartial = page.Where(f => req.InScope(f) &&
                (f.PartialHash is null ||
                 (f.Size > HashService.MiddleBlockThreshold && f.PartialVer < HashService.CurrentPartialVersion)))
                .ToList();
            if (needPartial.Count > 0)
            {
                HashByVolume(needPartial, partial: true, req, rep, ct);
                rep.VerifyVolumes();
                _files.UpdateComputedBatch(needPartial);
                result.PartialHashed += needPartial.Count;
            }
            ReportLargeBucketFraction(processed, bucketCount, partialVisited, 0.45, total, rep);
        }

        afterId = 0;
        long fullVisited = 0;
        while (true)
        {
            rep.WaitGate();
            ct.ThrowIfCancellationRequested();
            var page = _files.GetFullHashCandidatesForSizePage(
                size, afterId, LargeBucketPage, req.SessionId);
            if (page.Count == 0) break;
            afterId = page[^1].Id;
            fullVisited += page.Count;

            var needFull = page.Where(f => f.FullHash is null && req.InScope(f)).ToList();
            if (needFull.Count > 0)
            {
                rep.StageQuiet(ScanStage.FullHashing);
                HashByVolume(needFull, partial: false, req, rep, ct);
                rep.VerifyVolumes();
                _files.UpdateComputedBatch(needFull);
                result.FullHashed += needFull.Count;
            }
            ReportLargeBucketFraction(processed + (long)(bucketCount * 0.45), bucketCount,
                fullVisited, 0.50, total, rep);
        }

        Advance(bucketCount, rep, ref processed, total);
    }

    private static void ReportLargeBucketFraction(long completedBefore, long bucketCount,
        long pageVisited, double phaseWeight, long total, IExactScanReporter rep)
    {
        if (total <= 0 || bucketCount <= 0) return;
        double within = Math.Min(1, pageVisited / (double)bucketCount);
        rep.Fraction(Math.Min(1, (completedBefore + bucketCount * phaseWeight * within) / total));
        rep.SessionTick();
    }

    /// <summary>
    /// Обработка ПАЧКИ размеров целиком (этап 2.1 дорожной карты). Раньше HashByVolume вызывался
    /// на каждую корзину размера (обычно 2–10 файлов): на HDD головка металась между корзинами,
    /// а параллелизм томов не успевал наполниться. Теперь needPartial/needFull собираются по ВСЕМ
    /// корзинам пачки и хэшируются одним вызовом HashByVolume на фазу — партии в сотни файлов,
    /// которые к тому же сортируются по первому LCN на томах со штрафом позиционирования.
    /// Критерии отбора и байтовая семантика хэшей не менялись.
    /// </summary>
    private void ProcessSizeBatch(List<List<FileRecord>> buckets, ExactScanRequest req, IExactScanReporter rep,
        CancellationToken ct, ExactScanResult result, ref long processed, long total)
    {
        // Корзина — все файлы одного размера (включая отключённые тома с уже сохранёнными хэшами):
        // это даёт совпадения между подключённым и ранее сохранённым носителем (FR-40/41).
        var dupBuckets = new List<List<FileRecord>>(buckets.Count);

        // --- фаза 1: частичный хэш — кандидаты по всем корзинам пачки разом ---
        // 1.6: у крупных файлов (>128 МБ) частичный хэш v2 включает средний блок — строки со
        // старой версией пересчитываем (иначе смешение v1/v2 в одной корзине размера маскирует
        // дубликаты). Для мелких файлов алгоритм не менялся — их не трогаем.
        var needPartial = new List<FileRecord>();
        foreach (var sameSize in buckets)
        {
            if (sameSize.Count < 2) // защитно; запрос и так отдаёт только дубль-размеры
            {
                Advance(sameSize.Count, rep, ref processed, total);
                continue;
            }
            result.SizeCandidates += sameSize.Count;
            dupBuckets.Add(sameSize);
            foreach (var f in sameSize)
            {
                if (!req.InScope(f)) continue;
                if (f.PartialHash is null ||
                    (f.Size > HashService.MiddleBlockThreshold && f.PartialVer < HashService.CurrentPartialVersion))
                    needPartial.Add(f);
            }
        }
        if (needPartial.Count > 0)
        {
            HashByVolume(needPartial, partial: true, req, rep, ct);
            rep.VerifyVolumes();
            _files.UpdateComputedBatch(needPartial);
            result.PartialHashed += needPartial.Count;
        }

        // --- фаза 2: группировка по частичному → полный хэш только для коллизий, тоже пачкой ---
        var needFull = new List<FileRecord>();
        foreach (var sameSize in dupBuckets)
        {
            foreach (var grp in sameSize.Where(f => f.PartialHash is not null)
                         .GroupBy(f => Convert.ToHexString(f.PartialHash!)))
            {
                var candidates = grp.ToList();
                if (candidates.Count < 2) continue;
                needFull.AddRange(candidates.Where(f => f.FullHash is null && req.InScope(f)));
            }
            // Прогресс — по мере прохождения корзин пачки (как раньше processed += sameSize.Count).
            Advance(sameSize.Count, rep, ref processed, total);
        }
        if (needFull.Count > 0)
        {
            rep.StageQuiet(ScanStage.FullHashing);
            HashByVolume(needFull, partial: false, req, rep, ct);
            rep.VerifyVolumes();
            _files.UpdateComputedBatch(needFull);
            result.FullHashed += needFull.Count;
        }
    }

    private static void Advance(long bucketSize, IExactScanReporter rep, ref long processed, long total)
    {
        processed += bucketSize;
        if (total > 0) rep.Fraction(Math.Min(1.0, processed / (double)total));
        rep.SessionTick();
    }

    private void HashByVolume(List<FileRecord> files, bool partial, ExactScanRequest req,
        IExactScanReporter rep, CancellationToken ct)
    {
        VolumeParallelExecutor.RunByVolume(
            OrderForSequentialReads(files, req),
            f => f.VolumeId,
            vid => req.ReadPlans.TryGetValue(vid, out var p) ? p.ContentDop : Math.Max(1, req.RequestedParallelism),
            vid => req.ReadPlans.TryGetValue(vid, out var p) ? p.PhysicalDeviceKey : $"volume:{vid}",
            vid => req.ReadPlans.TryGetValue(vid, out var p) && p.PreferSequential,
            req.MaxVolumeParallelism,
            ct,
            f =>
            {
                rep.WaitGate();
                var path = FullPath(f, req.MountById);
                if (path is null || !File.Exists(LongPath.Fix(path))) return;
                try
                {
                    long bytesRead;
                    if (partial)
                    {
                        // Передаём ожидаемый размер: бросит FileChangedDuringScanException, если файл
                        // изменился между metadata-этапом и чтением — тогда хэш не сохраняется.
                        // 1.4: на носителе со штрафом позиционирования средние файлы читаем одним
                        // последовательным чтением (хэш тот же, на один сик меньше).
                        bool seq = req.ReadPlans.TryGetValue(f.VolumeId, out var plan) && plan.PreferSequential;
                        f.PartialHash = _hash.ComputePartialHash(path, f.Size, ct, preferWholeRead: seq);
                        bytesRead = HashService.EstimatePartialBytes(f.Size, seq);
                        f.PartialVer = HashService.CurrentPartialVersion;
                        if (f.ScanState < ScanState.Partial) f.ScanState = ScanState.Partial;
                    }
                    else
                    {
                        f.FullHash = _hash.ComputeFullHash(path, f.Size, ct);
                        bytesRead = f.Size;
                        f.ScanState = ScanState.Full;
                    }
                    rep.FileHashed(path, bytesRead);
                }
                catch (OperationCanceledException) { throw; }
                catch (FileChangedDuringScanException) { rep.ChangedSkipped(); }
                catch { rep.Skipped(); } // битый/занятый файл — пропуск (NFR-1)
            });
    }

    /// <summary>
    /// 2.1: на томах со штрафом позиционирования (план PreferSequential) сортирует файлы тома по
    /// первому LCN (FSCTL_GET_RETRIEVAL_POINTERS, прав администратора не нужно): чтения идут в
    /// порядке физического размещения на диске, и головка HDD движется в одну сторону, а не мечется.
    /// SSD не трогаем — сортировка дала бы только лишние syscall-ы. Ошибка получения LCN не
    /// критична: такой файл просто уходит в конец очереди своего тома (стабильно).
    /// </summary>
    private static List<FileRecord> OrderForSequentialReads(List<FileRecord> files, ExactScanRequest req)
    {
        if (files.Count < 2 || !OperatingSystem.IsWindows()) return files;
        if (!files.Any(f => req.ReadPlans.TryGetValue(f.VolumeId, out var p) && p.PreferSequential))
            return files;

        var ordered = new List<FileRecord>(files.Count);
        foreach (var volume in files.GroupBy(f => f.VolumeId)) // GroupBy стабилен: порядок внутри тома сохранён
        {
            var volumeFiles = volume.ToList();
            if (volumeFiles.Count > 1 &&
                req.ReadPlans.TryGetValue(volume.Key, out var plan) && plan.PreferSequential)
            {
                volumeFiles = SortByFirstLcn(volumeFiles, f =>
                {
                    var path = FullPath(f, req.MountById);
                    return path is null ? null : FastDedup.Engine.FileLayoutReader.TryGetFirstLcn(path);
                });
            }
            ordered.AddRange(volumeFiles); // RunByVolume группирует по тому заново — порядок МЕЖДУ томами не важен
        }
        return ordered;
    }

    /// <summary>
    /// Стабильная сортировка по первому LCN: по возрастанию, элементы без LCN — в конец с
    /// сохранением исходного порядка. Ключ вычисляется ровно один раз на элемент (получение LCN —
    /// syscall). Чистая функция — тестируема на синтетических данных.
    /// </summary>
    public static List<T> SortByFirstLcn<T>(IReadOnlyList<T> items, Func<T, long?> firstLcn)
    {
        var keyed = new (T Item, long? Lcn)[items.Count];
        for (int i = 0; i < items.Count; i++) keyed[i] = (items[i], firstLcn(items[i]));
        // OrderBy/ThenBy у LINQ стабильны: равные ключи (в т.ч. все null) остаются в исходном порядке.
        return keyed.OrderBy(t => t.Lcn is null ? 1 : 0).ThenBy(t => t.Lcn ?? 0).Select(t => t.Item).ToList();
    }

    private static string? FullPath(FileRecord f, IReadOnlyDictionary<long, string> mountById) =>
        mountById.TryGetValue(f.VolumeId, out var mp) ? Path.Combine(mp, f.RelPath) : null;
}
