using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Matching;

public interface IDuplicateGrouper
{
    /// <param name="inScope">
    /// Предикат «файл входит в текущую область сканирования». Группа строится ТОЛЬКО из in-scope
    /// файлов — результат соответствует тому, что пользователь выбрал сейчас, и не подмешивает
    /// файлы из старого индекса/других дисков (scope-aware grouping).
    /// </param>
    /// <param name="waitGate">
    /// Гейт паузы: вызывается в горячих циклах (побайтовая сверка читает файлы целиком — часто
    /// самый долгий этап). Раньше «Пауза» на этапе «Группировка» игнорировалась: UI показывал
    /// паузу, а диск продолжал молотить на полной скорости.
    /// </param>
    /// <param name="isPreferredKeep">
    /// «Файл в папке-эталоне» для правила Smart (2.10): строится фасадом (ScanService) из настроек
    /// и точек монтирования, Core настроек не знает. null — просто нет бонуса папки-эталона.
    /// </param>
    int BuildExactGroups(IReadOnlyDictionary<long, VolumeRecord> volumes, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null);

    int BuildSimilarImageGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null);

    int BuildSimilarAudioGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null);

    /// <summary>Удалить группы прошлых сессий, оставив только текущую (P0.1 scope-aware результаты).</summary>
    void ClearGroupsNotInSession(long sessionId);

    /// <summary>Удалить частично построенные группы текущей сессии (при отмене/сбое скана).</summary>
    void ClearGroupsInSession(long sessionId);
}

/// <summary>Формирование групп точных и похожих дубликатов (раздел 7).</summary>
public sealed class DuplicateGrouper : IDuplicateGrouper
{
    private const int FlushEvery = 500;
    private const int BucketPairCap = 1500;
    // Для умеренных наборов полный попарный перебор даёт 100% полноту (LSH может пропускать
    // пары с расстоянием > 7 бит). Для больших наборов переходим на LSH ради скорости.
    private const int FullPairwiseCap = 5000;

    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly LshRepository _lsh;
    private readonly IHashService _hash;

    public DuplicateGrouper(FileRepository files, GroupRepository groups, LshRepository lsh, IHashService hash)
    {
        _files = files;
        _groups = groups;
        _lsh = lsh;
        _hash = hash;
    }

    public void ClearGroupsNotInSession(long sessionId) => _groups.ClearGroupsNotInSession(sessionId);
    public void ClearGroupsInSession(long sessionId) => _groups.ClearGroupsInSession(sessionId);

    // ---------------- Точные дубликаты ----------------

    public int BuildExactGroups(IReadOnlyDictionary<long, VolumeRecord> volumes, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null)
    {
        // Чистим ТОЛЬКО точные группы текущей сессии (а не глобально): иначе пересборка в идущем скане
        // стирала бы результаты прошлой ЗАВЕРШЁННОЙ сессии ещё до того, как текущий скан успешно завершится
        // (раздел «Preserve previous completed results on cancel/fail»). Чистка прошлых сессий — только на успехе.
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.Exact);
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.ExactVideo);

        var pending = new List<DuplicateGroup>();
        // 2.5: накопитель кэша побайтовой сверки — пишется в БД пачками (той же каденцией FlushEvery),
        // чтобы не открывать транзакцию на каждую группу.
        var pendingVerified = new List<(long FileId, byte[] Hash)>();
        int created = 0;

        // 1.5: файлы выбираем ПАЧКАМИ хэшей (200 за запрос), а не по одному хэшу (N+1):
        // на больших библиотеках стадия Grouping была SQL-bound из-за десятков тысяч запросов.
        var sharedHashes = _files.GetSharedFullHashes(sessionId);
        const int HashBatch = 200;
        for (int bi = 0; bi < sharedHashes.Count; bi += HashBatch)
        {
            var hashBatch = sharedHashes.GetRange(bi, Math.Min(HashBatch, sharedHashes.Count - bi));
            foreach (var hashGroup in _files.GetByFullHashes(hashBatch, sessionId)
                         .GroupBy(f => System.IO.Hashing.XxHash128.HashToUInt128(f.FullHash!)))
            {
                ct.ThrowIfCancellationRequested();
                waitGate?.Invoke();
                // Scope-aware: группу строим ТОЛЬКО из файлов текущей области. Файлы с тем же хэшем из
                // старого индекса/других дисков сюда не попадают, поэтому результат соответствует именно
                // тому, что пользователь выбрал сейчас (нельзя удалить то, что не сканировали).
                var files = hashGroup.Where(inScope).ToList();
                if (files.Count < 2) continue;

                foreach (var vc in ByteVerifyClusters(files, volumes, ct, waitGate, pendingVerified))
                {
                    if (vc.Members.Count < 2) continue;

                    // Hardlink-aware: схлопываем алиасы (один физический файл под несколькими путями),
                    // иначе reclaimable завышается, а ссылки показываются как «лишние копии».
                    var (physical, linkCounts) = CollapseHardlinks(vc.Members, volumes);
                    if (physical.Count < 2) continue; // вся группа — hardlink-и на один файл: удалять нечего

                    var rep = RepresentativeSelector.Pick(physical, rule, isPreferredKeep);
                    bool isVideo = physical[0].Category == FileCategory.Video;

                    // Освобождаемое место: размер не-эталонного файла считаем ТОЛЬКО если у его inode нет
                    // других ссылок (LinkCount<=1). Если файл — один из нескольких hardlink-ов (в т.ч.
                    // схлопнутых или внешних), удаление одного пути не освободит блоки — не завышаем оценку.
                    // Для подключённых файлов берём РАЗМЕР НА ДИСКЕ (GetCompressedFileSize): sparse и
                    // NTFS-сжатые файлы освобождают выделенные блоки, а не логический размер — иначе
                    // «можно освободить 10 ГБ» при реальных 100 МБ.
                    long reclaimable = 0;
                    foreach (var f in physical)
                    {
                        if (f.Id == rep.Id) continue;
                        bool freeable = !linkCounts.TryGetValue(f.Id, out var lc) || lc <= 1;
                        if (!freeable) continue;
                        var fPath = Resolve(f, volumes);
                        reclaimable += fPath is not null ? SizeOnDisk(fPath, f.Size) : f.Size;
                    }

                    var group = new DuplicateGroup
                    {
                        MatchType = isVideo ? MatchType.ExactVideo : MatchType.Exact,
                        // Полностью сверенные байт-в-байт группы — 1.0; hash-only (есть члены, которые
                        // нельзя было прочитать/сверить) помечаем < 1.0, чтобы их нельзя было автоудалять.
                        Confidence = vc.ByteVerified ? 1.0 : HashOnlyExactConfidence,
                        RepresentativeId = rep.Id,
                        ReclaimableBytes = reclaimable
                    };
                    foreach (var f in physical)
                        group.Members.Add(new DuplicateMember { FileId = f.Id, Similarity = 1.0, File = f });

                    pending.Add(group);
                    created++;
                }

                if (pending.Count >= FlushEvery)
                {
                    _groups.InsertGroups(pending, sessionId);
                    pending.Clear();
                    FlushVerified(pendingVerified);
                    onGroups?.Invoke(created);
                }
            }
        }
        if (pending.Count > 0) _groups.InsertGroups(pending, sessionId);
        FlushVerified(pendingVerified);
        onGroups?.Invoke(created);
        return created;
    }

    /// <summary>Сброс накопленного кэша сверки в БД. Кэш — оптимизация: его сбой логируем,
    /// но группировку не роняем (следующий скан просто сверит чтением заново).</summary>
    private void FlushVerified(List<(long FileId, byte[] Hash)> pendingVerified)
    {
        if (pendingVerified.Count == 0) return;
        try { _files.UpdateVerifiedFullHashBatch(pendingVerified); }
        catch (Exception ex) { AppLog.Error("UpdateVerifiedFullHashBatch", ex); }
        pendingVerified.Clear();
    }

    private const double HashOnlyExactConfidence = 0.99;

    /// <summary>Фактически занимаемое на диске место (sparse/сжатые меньше логического размера).</summary>
    private static long SizeOnDisk(string path, long logicalSize)
    {
        if (!OperatingSystem.IsWindows()) return logicalSize;
        try
        {
            uint low = GetCompressedFileSizeW(LongPath.Fix(path), out uint high);
            if (low == uint.MaxValue && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 0)
                return logicalSize;
            return ((long)high << 32) | low;
        }
        catch { return logicalSize; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>Кластер-кандидат точных дубликатов и признак того, что он сверен байт-в-байт.</summary>
    private sealed record VerifiedCluster(List<FileRecord> Members, bool ByteVerified);

    /// <summary>
    /// Кластеризация файлов с совпавшим полным хэшем побайтовой сверкой (FR-11).
    ///
    /// Якорем кластера и участником сверки может быть ТОЛЬКО подключённый и реально читаемый файл.
    /// Это закрывает две опасные дыры предыдущей версии (сверка только с «головой» кластера):
    ///   • нечитаемый файл-голова «отравлял» всю корзину и ложно разрывал настоящую пару;
    ///   • офлайн-голова коротко замыкала сверку и могла ложно объединить два РАЗНЫХ подключённых
    ///     файла в группу с confidence 1.0 (риск удаления непохожих данных при коллизии хэша).
    ///
    /// Подключённые читаемые файлы кластеризуются строгой попарной сверкой друг с другом. Офлайн/
    /// нечитаемые файлы нельзя проверить — они подвешиваются как hash-only члены (группа получает
    /// confidence &lt; 1.0 и не подлежит автоудалению).
    ///
    /// 2.5: кэш сверки (verified_full_hash). Файл с валидным кэшем (значение байтово равно его
    /// full_hash) уже проходил побайтовую сверку против содержимого с этим хэшем, а его метаданные
    /// с тех пор строго переиспользованы (любой другой путь — UpsertMetaBatch или пересчёт full_hash —
    /// обнуляет кэш). Такие файлы кластеризуются между собой БЕЗ чтения; новые/сброшенные сверяются,
    /// как раньше, чтением против головы кластера. RevalidateBeforeDelete в UI это не отменяет —
    /// перед удалением содержимое всегда пересчитывается заново (последний рубеж).
    /// В <paramref name="verifiedOut"/> добавляются члены успешно сверенного кластера для записи кэша.
    /// </summary>
    private List<VerifiedCluster> ByteVerifyClusters(
        List<FileRecord> files, IReadOnlyDictionary<long, VolumeRecord> volumes, CancellationToken ct,
        Action? waitGate = null, List<(long FileId, byte[] Hash)>? verifiedOut = null)
    {
        var readable = new List<(FileRecord Rec, string Path)>();
        var unverifiable = new List<FileRecord>(); // отключён ИЛИ подключён, но не читается
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Resolve(f, volumes);
            if (path is not null && File.Exists(LongPath.Fix(path)) && IsReadable(path))
                readable.Add((f, path));
            else
                unverifiable.Add(f);
        }

        // Подключённые читаемые: кластеризуем строгой попарной сверкой. Нечитаемый файл сюда не
        // попадает, поэтому не может быть «головой» и испортить решение для остальных.
        // 1.3: если ОБА файла на носителях со штрафом позиционирования — сверяем крупными блоками
        // (16 МБ вместо 1 МБ): чередование головки A/B сокращается в ~16 раз, пара 2 ГБ на одном
        // HDD — десятки секунд позиционирования → секунды.
        const int LargeVerifyBlock = 16 << 20;
        static bool Seq(IReadOnlyDictionary<long, VolumeRecord> vols, long volumeId) =>
            vols.TryGetValue(volumeId, out var v) && v.PreferSequentialReads;

        var clusters = new List<List<FileRecord>>();
        var headPaths = new List<string>();
        var headSeq = new List<bool>();

        // 2.5: сначала — файлы с валидным кэшем: они сверялись против ОДИНАКОВОГО содержимого
        // (одного full_hash) и объединяются без чтения. Группировка идёт по XxHash128-ключу от
        // блоба full_hash, поэтому защитно сравниваем сами блобы: разные full_hash (теоретическая
        // коллизия ключа) в один кластер не попадают.
        var fresh = new List<(FileRecord Rec, string Path)>();
        foreach (var (rec, path) in readable)
        {
            if (!HasValidVerifiedCache(rec)) { fresh.Add((rec, path)); continue; }
            int idx = clusters.FindIndex(c => c[0].FullHash!.AsSpan().SequenceEqual(rec.FullHash!));
            if (idx >= 0) clusters[idx].Add(rec);
            else
            {
                clusters.Add(new List<FileRecord> { rec });
                headPaths.Add(path); // голова смешанного кластера: новые файлы сверяются с ней чтением
                headSeq.Add(Seq(volumes, rec.VolumeId));
            }
        }

        foreach (var (rec, path) in fresh)
        {
            ct.ThrowIfCancellationRequested();
            waitGate?.Invoke(); // сверка читает файлы целиком — пауза обязана работать именно здесь
            bool recSeq = Seq(volumes, rec.VolumeId);
            bool placed = false;
            for (int i = 0; i < clusters.Count; i++)
            {
                bool equal;
                int block = recSeq && headSeq[i] ? LargeVerifyBlock : 0;
                try { equal = _hash.FilesAreByteEqual(headPaths[i], path, ct, block); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Транзиентная ошибка чтения уже проверенного как читаемый файла — не объединяем
                    // с этим кластером, пробуем следующий (файл не теряем и не якорим ложно).
                    AppLog.Write($"Побайтовая сверка не удалась для «{path}» — не объединяем с кластером.");
                    continue;
                }
                if (equal) { clusters[i].Add(rec); placed = true; break; }
            }
            if (!placed)
            {
                clusters.Add(new List<FileRecord> { rec });
                headPaths.Add(path);
                headSeq.Add(recSeq); // без этого headSeq[i] падал на 2-м файле HDD-тома (латентный баг)
            }
        }

        // 2.5: запись кэша — ТОЛЬКО когда все читаемые файлы сошлись в ОДИН байт-сверенный кластер.
        // Два и более кластера означают разные содержимые с одним full_hash (коллизия хэша или сбой
        // чтения): кэшировать нельзя, иначе повторный скан склеил бы разные байты без чтения.
        // Кластер-одиночка тоже не кэшируется: его файл ни с чем не сверялся (вакуумная «сверка»),
        // а кэш обязан означать реально выполненную побайтовую сверку хотя бы с одной копией — иначе
        // два никогда не сравнивавшихся одиночки позже склеились бы в группу 1.0 без единого чтения.
        if (clusters.Count == 1 && clusters[0].Count >= 2 && verifiedOut is not null)
        {
            foreach (var rec in clusters[0])
            {
                if (rec.FullHash is null || HasValidVerifiedCache(rec)) continue;
                rec.VerifiedFullHash = rec.FullHash; // и в памяти — инвариант тот же, что в БД
                verifiedOut.Add((rec.Id, rec.FullHash));
            }
        }
        else if (clusters.Count > 1)
        {
            AppLog.Write($"Побайтовая сверка: {clusters.Count} разных содержимых при одном full_hash — кэш сверки не записывается.");
        }

        var result = clusters.Select(c => new VerifiedCluster(c, ByteVerified: true)).ToList();

        // Офлайн/непроверяемые (тот же full_hash): сверить нечем. Подвешиваем к первому байт-сверенному
        // кластеру как hash-only (FR-40/41 кросс-том), понижая его до неверифицированного; если сверенных
        // кластеров нет — формируем отдельную hash-only группу. Так офлайн-файл НИКОГДА не объединяет
        // два подключённых файла без прямой сверки.
        if (unverifiable.Count > 0)
        {
            if (result.Count > 0)
            {
                result[0].Members.AddRange(unverifiable);
                result[0] = result[0] with { ByteVerified = false };
            }
            else if (unverifiable.Count >= 2)
            {
                result.Add(new VerifiedCluster(new List<FileRecord>(unverifiable), ByteVerified: false));
            }
        }
        return result;
    }

    /// <summary>
    /// Кэш сверки валиден: verified_full_hash присутствует и БАЙТОВО равен текущему full_hash (2.5).
    /// Любое другое значение (в т.ч. от прежнего full_hash) игнорируется — файл сверяется чтением.
    /// </summary>
    private static bool HasValidVerifiedCache(FileRecord f) =>
        f.VerifiedFullHash is not null && f.FullHash is not null &&
        f.VerifiedFullHash.AsSpan().SequenceEqual(f.FullHash);

    /// <summary>Можно ли реально прочитать файл (а не только что он «существует»). Дешёвая проба 1 байта.</summary>
    private static bool IsReadable(string path)
    {
        // Облачный плейсхолдер «читается», но проба/сверка заставит OneDrive скачать содержимое
        // из облака — считаем его непроверяемым: файл уйдёт в hash-only
        // члены (Confidence < 1.0, автоудаление запрещено), трафика не будет.
        if (CloudFiles.IsPlaceholderPath(path)) return false;
        try
        {
            using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> one = stackalloc byte[1];
            fs.Read(one); // 0 для пустого файла — это тоже «читаем»
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Схлопывает hardlink-алиасы внутри кластера: несколько путей к ОДНОМУ физическому файлу
    /// (одинаковые VolumeSerial+FileIndex) считаются одной копией. Так освобождаемое место не
    /// завышается, а ссылки не показываются как «лишние копии». Идентичность есть только у
    /// подключённых файлов; недоступные считаем отдельными (консервативно).
    /// </summary>
    private static (List<FileRecord> Physical, Dictionary<long, uint> LinkCounts) CollapseHardlinks(
        List<FileRecord> cluster, IReadOnlyDictionary<long, VolumeRecord> volumes)
    {
        var linkCounts = new Dictionary<long, uint>();
        if (!OperatingSystem.IsWindows() || cluster.Count < 2) return (cluster, linkCounts);

        var seen = new HashSet<(uint VolumeSerial, ulong FileIndex)>();
        var physical = new List<FileRecord>(cluster.Count);
        foreach (var f in cluster)
        {
            var path = Resolve(f, volumes);
            if (path is not null &&
                FastDedup.Engine.FileIdentityReader.TryGet(LongPath.Fix(path), out var id))
            {
                if (!seen.Add((id.VolumeSerialNumber, id.FileIndex)))
                    continue; // тот же физический файл — это алиас, не отдельная копия
                linkCounts[f.Id] = id.LinkCount; // число hardlink-ов на этот inode (для расчёта reclaimable)
            }
            physical.Add(f);
        }
        return (physical, linkCounts);
    }

    // ---------------- Похожие изображения ----------------

    // 3.6: параметры потоковой группировки похожих фото — размер страницы лёгкой выборки
    // прохода 1 и чанк вставки LSH-строк (строки не копятся все разом: 8 строк на файл —
    // на 1М фото это был бы список на сотни МБ).
    private const int SlimPageSize = 5000;
    private const int LshInsertChunk = 100_000;

    public int BuildSimilarImageGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null)
    {
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.SimilarImage);
        _lsh.ClearKind(MatchType.SimilarImage);

        // 3.6: двухпроходная потоковая группировка. Раньше вся фототека материализовалась разом
        // (тяжёлые FileRecord с блобами хэшей — пик ~1 ГБ на 1М фото); теперь ПРОХОД 1 идёт по
        // лёгкой выборке (перцептивные признаки + поля предикатов, постранично keyset по id), а
        // полные записи дотягиваются ПРОХОДОМ 2 только для файлов, попавших в группы (обычно малая
        // доля фототеки). Семантика групп не меняется: тот же DSU, те же пороги, тот же выбор
        // эталона (включая Sharpness-ветку 3.7), inScope применяется, как раньше, к каждой записи
        // (слим-выборка содержит все поля, которые читают предикаты: том/путь/категорию/размер/даты).
        var hashOf = new Dictionary<long, ulong>();
        var orderedIds = new List<long>();
        var dsu = new Dsu();
        // Блобы pixel_hash живут только в этом словаре и освобождаются с концом прохода 1.
        var pixelSeen = new Dictionary<UInt128, List<(byte[] Blob, long FirstId)>>();
        var phashSeen = new Dictionary<ulong, long>();
        var buckets = new Dictionary<(int, long), List<long>>();
        var lshRows = new List<(int, long, long)>();

        long afterId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            waitGate?.Invoke();
            // Scope-aware: SQL-фильтр по области сессии + предикат inScope как страховка (как раньше).
            var page = _files.GetPerceptualSlimPage(FileCategory.Image, afterId, SlimPageSize, sessionId);
            if (page.Count == 0) break;
            afterId = page[^1].Id;

            foreach (var f in page)
            {
                if (!inScope(f)) continue;
                var h = PerceptualLsh.ToHash(f.PHash);
                hashOf[f.Id] = h;
                orderedIds.Add(f.Id);
                dsu.MakeSet(f.Id);

                // 1) Точное совпадение pixel-hash → одинаковое изображение, иные метаданные (FR-14).
                UnionByExactBlobIncremental(pixelSeen, f.PixelHash, f.Id, dsu);
                // 2) Точное совпадение pHash — дёшево объединяем.
                if (h != 0)
                {
                    if (phashSeen.TryGetValue(h, out var first)) dsu.Union(first, f.Id);
                    else phashSeen[h] = f.Id;
                }
                // 3) Корзины LSH; строки в БД пишем чанками, не копя весь список.
                foreach (var (band, key) in PerceptualLsh.BandKeys(h))
                {
                    var bk = (band, key);
                    if (!buckets.TryGetValue(bk, out var list)) buckets[bk] = list = new List<long>();
                    list.Add(f.Id);
                    lshRows.Add((band, key, f.Id));
                }
                if (lshRows.Count >= LshInsertChunk)
                {
                    _lsh.InsertBuckets(MatchType.SimilarImage, lshRows);
                    lshRows.Clear();
                }
            }
        }

        // Как раньше: меньше двух картинок в области — ни групп, ни LSH-строк (первый сброс чанка
        // требует десятков тысяч in-scope файлов, поэтому здесь заведомо ничего не записано).
        if (orderedIds.Count < 2) return 0;
        if (lshRows.Count > 0)
        {
            _lsh.InsertBuckets(MatchType.SimilarImage, lshRows);
            lshRows.Clear();
        }
        pixelSeen.Clear(); // блобы pixel_hash дальше не нужны — освобождаем до попарных сравнений
        phashSeen.Clear();

        var allDistinct = DistinctByHash(orderedIds, hashOf);
        if (allDistinct.Count <= FullPairwiseCap)
        {
            // Полный перебор — гарантированная полнота для типовых библиотек.
            for (int i = 0; i < allDistinct.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                waitGate?.Invoke();
                for (int j = i + 1; j < allDistinct.Count; j++)
                {
                    if (PerceptualLsh.Similarity(hashOf[allDistinct[i]], hashOf[allDistinct[j]]) >= threshold)
                        dsu.Union(allDistinct[i], allDistinct[j]);
                }
            }
        }
        else
        {
            // Большой набор — кандидаты только из общих корзин LSH. Крупные корзины НЕ отбрасываем
            // (это раньше приводило к молчаливому пропуску дубликатов), а дробим по доп. полосам.
            foreach (var (_, list) in buckets)
            {
                ct.ThrowIfCancellationRequested();
                waitGate?.Invoke();
                var distinct = DistinctByHash(list, hashOf);
                if (distinct.Count < 2) continue;
                UnionSimilarInBucket(distinct, BucketPairCap,
                    (a, b) => PerceptualLsh.Similarity(hashOf[a], hashOf[b]) >= threshold,
                    (id, level) => PerceptualLsh.BandKey(hashOf[id], level), PerceptualLsh.Bands,
                    dsu, ct, "image");
            }
        }

        // ПРОХОД 2: полные записи (Sharpness для эталона 3.7, PixelHash для похожести 1.0, пути
        // и метаданные для UI) дотягиваются батчами ТОЛЬКО для членов будущих групп.
        var groupedIds = new List<long>();
        foreach (var (_, ids) in dsu.Groups())
            if (ids.Count >= 2) groupedIds.AddRange(ids);
        var byId = new Dictionary<long, FileRecord>(groupedIds.Count);
        foreach (var rec in _files.GetByIds(groupedIds)) byId[rec.Id] = rec;

        return PersistSimilarGroups(dsu, byId, MatchType.SimilarImage, rule, onGroups,
            (rep, member) => SimilarityImage(hashOf, rep, member), ct, sessionId, isPreferredKeep);
    }

    private static double SimilarityImage(Dictionary<long, ulong> hashOf, FileRecord rep, FileRecord m)
    {
        if (rep.PixelHash is not null && m.PixelHash is not null && rep.PixelHash.AsSpan().SequenceEqual(m.PixelHash))
            return 1.0;
        return PerceptualLsh.Similarity(hashOf[rep.Id], hashOf[m.Id]);
    }

    // ---------------- Похожая музыка ----------------

    public int BuildSimilarAudioGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0,
        Action? waitGate = null, Func<FileRecord, bool>? isPreferredKeep = null)
    {
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.SimilarAudio);
        _lsh.ClearKind(MatchType.SimilarAudio);

        // Scope-aware: похожие ищем только среди файлов текущей области (не по всей базе) — scope-aware SQL
        // по сессии + предикат inScope как страховка.
        var tracks = _files.GetByCategoryWithPerceptual(FileCategory.Audio, sessionId).Where(inScope).ToList();
        if (tracks.Count < 2) return 0;

        var byId = tracks.ToDictionary(f => f.Id);
        var minhash = new MinHasher();
        var sigOf = new Dictionary<long, uint[]>(tracks.Count);
        foreach (var f in tracks)
        {
            var set = Deduplicator.Core.Audio.AudioFingerprint.ToSet(f.AudioFp!);
            sigOf[f.Id] = minhash.Signature(set);
        }

        var dsu = new Dsu();
        foreach (var f in tracks) dsu.MakeSet(f.Id);

        // Идентичные отпечатки (та же запись в разных контейнерах с тем же потоком) — объединяем сразу,
        // как и для изображений с одинаковым pHash. Делает обработку согласованной image/audio.
        UnionByExactBlob(tracks, f => f.AudioFp, dsu);

        var buckets = new Dictionary<(int, long), List<long>>();
        var lshRows = new List<(int, long, long)>();
        foreach (var f in tracks)
        {
            foreach (var (band, key) in MinHasher.BandKeys(sigOf[f.Id]))
            {
                var bk = (band, key);
                if (!buckets.TryGetValue(bk, out var list)) buckets[bk] = list = new List<long>();
                list.Add(f.Id);
                lshRows.Add((band, key, f.Id));
            }
        }
        _lsh.InsertBuckets(MatchType.SimilarAudio, lshRows);

        var allIds = tracks.Select(f => f.Id).ToList();
        if (allIds.Count <= FullPairwiseCap)
        {
            for (int i = 0; i < allIds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                waitGate?.Invoke();
                for (int j = i + 1; j < allIds.Count; j++)
                {
                    if (EstimatedJaccard(sigOf[allIds[i]], sigOf[allIds[j]]) >= threshold)
                        dsu.Union(allIds[i], allIds[j]);
                }
            }
        }
        else
        {
            foreach (var (_, list) in buckets)
            {
                ct.ThrowIfCancellationRequested();
                waitGate?.Invoke();
                if (list.Count < 2) continue;
                UnionSimilarInBucket(list, BucketPairCap,
                    (a, b) => EstimatedJaccard(sigOf[a], sigOf[b]) >= threshold,
                    (id, level) => MinHasher.BandKey(sigOf[id], level), MinHasher.Bands,
                    dsu, ct, "audio");
            }
        }

        return PersistSimilarGroups(dsu, byId, MatchType.SimilarAudio, rule, onGroups,
            (rep, member) => EstimatedJaccard(sigOf[rep.Id], sigOf[member.Id]), ct, sessionId, isPreferredKeep);
    }

    private static double EstimatedJaccard(uint[] a, uint[] b)
    {
        int eq = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) eq++;
        return eq / (double)a.Length;
    }

    // ---------------- Общее для похожих ----------------

    private int PersistSimilarGroups(Dsu dsu, Dictionary<long, FileRecord> byId,
        MatchType type, RepresentativeRule rule, Action<long>? onGroups,
        Func<FileRecord, FileRecord, double> similarity, CancellationToken ct, long sessionId,
        Func<FileRecord, bool>? isPreferredKeep = null)
    {
        var pending = new List<DuplicateGroup>();
        int created = 0;

        foreach (var (_, ids) in dsu.Groups())
        {
            ct.ThrowIfCancellationRequested();
            if (ids.Count < 2) continue;
            // 3.6: для изображений byId — дотянутые ПОЛНЫЕ записи (проход 2); строка могла исчезнуть
            // из БД между проходами (внешнее изменение) — такую пропускаем, а не падаем на byId[id].
            var members = new List<FileRecord>(ids.Count);
            foreach (var id in ids)
                if (byId.TryGetValue(id, out var rec)) members.Add(rec);
            if (members.Count < 2) continue;
            var rep = PickSimilarRepresentative(members, type, rule, isPreferredKeep);

            var group = new DuplicateGroup
            {
                MatchType = type,
                RepresentativeId = rep.Id,
                // Похожие — НЕ точные копии. «Освобождаемое место» здесь не считаем (P0.3): иначе
                // «можно освободить X ГБ» подталкивает к удалению неодинаковых файлов. Экономия
                // станет известна только после ручного выбора в UI.
                ReclaimableBytes = 0
            };

            double sumSim = 0;
            foreach (var m in members)
            {
                double sim = m.Id == rep.Id ? 1.0 : Math.Clamp(similarity(rep, m), 0, 1);
                sumSim += sim;
                group.Members.Add(new DuplicateMember { FileId = m.Id, Similarity = sim, File = m });
            }
            group.Confidence = sumSim / members.Count;
            pending.Add(group);
            created++;

            if (pending.Count >= FlushEvery)
            {
                _groups.InsertGroups(pending, sessionId);
                pending.Clear();
                onGroups?.Invoke(created);
            }
        }
        if (pending.Count > 0) _groups.InsertGroups(pending, sessionId);
        onGroups?.Invoke(created);
        return created;
    }

    /// <summary>
    /// 3.7 «Лучший кадр серии»: эталон группы ПОХОЖИХ ФОТО (серийная съёмка — несколько дублей
    /// одного кадра) подсказываем по максимальной резкости, если она посчитана хотя бы у двух
    /// членов; при равенстве максимума тай-брейк — обычное правило rule через Pick. Для аудио и
    /// при нехватке данных (старые строки с sharpness NULL) — прежний Pick по правилу. Точные
    /// группы (BuildExactGroups) этой ветки не касаются. Это ТОЛЬКО подсказка выбора эталона:
    /// политика удаления похожих не меняется — они отмечаются исключительно вручную.
    /// </summary>
    private static FileRecord PickSimilarRepresentative(List<FileRecord> members, MatchType type,
        RepresentativeRule rule, Func<FileRecord, bool>? isPreferredKeep)
    {
        if (type == MatchType.SimilarImage)
        {
            var withSharpness = members.Where(f => f.Sharpness is not null).ToList();
            if (withSharpness.Count >= 2)
            {
                double max = withSharpness.Max(f => f.Sharpness!.Value);
                var best = withSharpness.Where(f => f.Sharpness!.Value >= max).ToList();
                return best.Count == 1 ? best[0] : RepresentativeSelector.Pick(best, rule, isPreferredKeep);
            }
        }
        return RepresentativeSelector.Pick(members, rule, isPreferredKeep);
    }

    private static void UnionByExactBlob(List<FileRecord> files, Func<FileRecord, byte[]?> selector, Dsu dsu)
    {
        // Ключ — XxHash128 от блоба (16 байт), а не hex-строка всего блоба: аудио-отпечатки
        // весят ~3,8 КБ, hex-строка — ~15 КБ UTF-16 на трек, и почти все они (отпечатки уникальны)
        // жили в словаре до конца метода — ~4x суммарного объёма отпечатков лишней памяти (B8).
        // Коллизия XxHash128 отсекается прямой сверкой блобов внутри корзины.
        var seen = new Dictionary<UInt128, List<(byte[] Blob, long FirstId)>>();
        foreach (var f in files)
            UnionByExactBlobIncremental(seen, selector(f), f.Id, dsu);
    }

    /// <summary>Инкрементальный шаг <see cref="UnionByExactBlob"/> — для потокового прохода 1 (3.6),
    /// где файлы приходят страницами и весь список сразу недоступен. Семантика идентична пакетной
    /// версии: тот же ключ XxHash128, та же сверка блобов против коллизий.</summary>
    private static void UnionByExactBlobIncremental(
        Dictionary<UInt128, List<(byte[] Blob, long FirstId)>> seen, byte[]? blob, long id, Dsu dsu)
    {
        if (blob is null) return;
        var h = System.IO.Hashing.XxHash128.HashToUInt128(blob);
        if (!seen.TryGetValue(h, out var bucket))
        {
            seen[h] = new List<(byte[], long)>(1) { (blob, id) };
            return;
        }
        var hit = bucket.FirstOrDefault(b => b.Blob.AsSpan().SequenceEqual(blob));
        if (hit.Blob is not null) dsu.Union(hit.FirstId, id);
        else bucket.Add((blob, id));
    }

    private static List<long> DistinctByHash(List<long> ids, Dictionary<long, ulong> hashOf)
    {
        var seen = new HashSet<ulong>();
        var result = new List<long>();
        foreach (var id in ids)
            if (seen.Add(hashOf[id])) result.Add(id);
        return result;
    }

    /// <summary>
    /// Объединяет похожие элементы внутри корзины LSH. Крупные корзины НЕ отбрасываем (это вело к
    /// молчаливому пропуску дубликатов), а дробим по дополнительным полосам, пока подкорзины не
    /// станут ≤ pairCap, и только тогда сравниваем попарно. Полнота для близких пар сохраняется,
    /// объём работы остаётся ограниченным. Рекурсия конечна: level строго растёт до maxLevels.
    /// </summary>
    public static void UnionSimilarInBucket(
        IReadOnlyList<long> ids, int pairCap,
        Func<long, long, bool> similar,
        Func<long, int, long> refineKey, int maxLevels,
        Dsu dsu, CancellationToken ct = default, string kindForLog = "")
    {
        Refine(ids, 0);

        void Refine(IReadOnlyList<long> bucket, int level)
        {
            if (bucket.Count < 2) return;

            if (bucket.Count <= pairCap || level >= maxLevels)
            {
                if (bucket.Count > pairCap)
                    AppLog.Write($"LSH {kindForLog}: остаточная корзина {bucket.Count} элементов — сравнение впритык (почти идентичные хэши).");
                for (int i = 0; i < bucket.Count; i++)
                for (int j = i + 1; j < bucket.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (similar(bucket[i], bucket[j])) dsu.Union(bucket[i], bucket[j]);
                }
                return;
            }

            // Дробим корзину по ключу следующей полосы.
            var sub = new Dictionary<long, List<long>>();
            foreach (var id in bucket)
            {
                var key = refineKey(id, level);
                if (!sub.TryGetValue(key, out var lst)) sub[key] = lst = new List<long>();
                lst.Add(id);
            }
            // Если эта полоса не разделила корзину — пробуем следующую (level гарантированно растёт).
            if (sub.Count <= 1) { Refine(bucket, level + 1); return; }
            foreach (var (_, lst) in sub) Refine(lst, level + 1);
        }
    }

    private static string? Resolve(FileRecord f, IReadOnlyDictionary<long, VolumeRecord> volumes)
    {
        if (volumes.TryGetValue(f.VolumeId, out var v) && v.IsConnected && v.MountPath is not null)
            return Path.Combine(v.MountPath, f.RelPath);
        return null;
    }
}
