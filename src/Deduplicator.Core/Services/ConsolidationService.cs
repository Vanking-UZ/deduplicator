using Deduplicator.Core.Data;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IConsolidationService
{
    /// <summary>Построить план: какие файлы уникальны (копировать), какие — дубликаты (пропустить).</summary>
    ConsolidationPlan BuildPlan(ConsolidationOptions options);

    /// <summary>Выполнить план: скопировать уникальное содержимое в целевое хранилище.</summary>
    ConsolidationReport Execute(ConsolidationPlan plan, ConsolidationOptions options,
        Action<int, int, string>? onProgress, CancellationToken ct);
}

/// <summary>Консолидация разрозненных носителей в один дедуплицированный архив.</summary>
public sealed class ConsolidationService : IConsolidationService
{
    // 3.5: порог схлопывания ПОХОЖИХ фото. СОЗНАТЕЛЬНО жёстче пользовательского порога поиска
    // похожих (0.80–0.97): консолидация решает, что НЕ копировать в архив, поэтому схлопываются
    // только почти неразличимые кадры — схожесть ≥ 0.97 означает не более 1 бита различия pHash из 64.
    private const double SimilarCollapseThreshold = 0.97;

    private const int CatalogPageSize = 2_048;

    private readonly FileRepository _files;
    private readonly IVolumeService _volumeService;

    public ConsolidationService(FileRepository files, IVolumeService volumeService)
    {
        _files = files;
        _volumeService = volumeService;
    }

    public ConsolidationPlan BuildPlan(ConsolidationOptions options)
    {
        var catalog = _volumeService.SyncAndGetCatalog();
        var byId = catalog.ToDictionary(v => v.Id);

        var plan = new ConsolidationPlan();

        // Сначала решаем, ЧТО копировать (эталоны точных групп + бесхэшевые), и только потом
        // назначаем целевые пути: схлопывание похожих (3.5) идёт между этими шагами, чтобы имена
        // схлопнутых не резервировались впустую и не плодили суффиксы __N у оставшихся файлов.
        var chosenToCopy = new List<(FileRecord File, int Siblings)>();

        var sourceIds = options.SourceVolumeIds.Where(id => id > 0).Distinct().ToArray();

        // Бесхэшевые файлы независимы: добавляем их к будущему плану сразу, keyset-страницами.
        long afterId = 0;
        while (true)
        {
            var page = _files.GetConsolidationPage(
                volumeIds: sourceIds,
                withFullHash: false,
                afterId: afterId,
                afterFullHash: null,
                limit: CatalogPageSize);
            if (page.Count == 0) break;
            foreach (var f in page)
            {
                if (byId.TryGetValue(f.VolumeId, out var v)) f.VolumeLabel = v.DisplayName;
                chosenToCopy.Add((f, 0));
            }
            afterId = page[^1].Id;
        }

        // Хэшированные строки отсортированы БД по (full_hash,id). Держим только текущую exact-группу;
        // на границе хэша сразу публикуем representative/skip в план. Пиковая дополнительная память —
        // размер крупнейшей группы, а не количество файлов всех выбранных томов.
        byte[]? cursorHash = null;
        afterId = 0;
        var currentExactGroup = new List<FileRecord>();
        byte[]? currentHash = null;
        while (true)
        {
            var page = _files.GetConsolidationPage(
                volumeIds: sourceIds,
                withFullHash: true,
                afterId: afterId,
                afterFullHash: cursorHash,
                limit: CatalogPageSize);
            if (page.Count == 0) break;
            foreach (var f in page)
            {
                if (byId.TryGetValue(f.VolumeId, out var v)) f.VolumeLabel = v.DisplayName;
                if (currentHash is not null && !currentHash.AsSpan().SequenceEqual(f.FullHash!))
                {
                    FinishExactGroup(currentExactGroup, byId, options.Rule, plan, chosenToCopy);
                    currentExactGroup.Clear();
                }
                currentHash = f.FullHash!;
                currentExactGroup.Add(f);
            }
            var last = page[^1];
            cursorHash = last.FullHash;
            afterId = last.Id;
        }
        FinishExactGroup(currentExactGroup, byId, options.Rule, plan, chosenToCopy);

        // 3.5: из группы почти одинаковых фото в архив едет один кадр; схлопнутые уходят
        // в plan.CollapsedSimilar (копироваться не будут, источники не трогаются).
        if (options.CollapseSimilarPhotos)
            chosenToCopy = CollapseSimilarPhotos(chosenToCopy, plan);

        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, siblings) in chosenToCopy)
        {
            plan.ToCopy.Add(new ConsolidationItem
            {
                File = file,
                WillCopy = true,
                TargetRelPath = UniqueTarget(file, options, usedTargets),
                DuplicateSiblings = siblings
            });
        }

        return plan;
    }

    private static void FinishExactGroup(
        List<FileRecord> members,
        IReadOnlyDictionary<long, VolumeRecord> volumes,
        RepresentativeRule rule,
        ConsolidationPlan plan,
        List<(FileRecord File, int Siblings)> chosenToCopy)
    {
        if (members.Count == 0) return;
        // Предпочитаем копию на ПОДКЛЮЧЁННОМ носителе, чтобы файл реально скопировался
        // (иначе при отключённом эталоне содержимое не попало бы в архив).
        var connectedMembers = members
            .Where(m => volumes.TryGetValue(m.VolumeId, out var v) && v.IsConnected)
            .ToList();
        var pickFrom = connectedMembers.Count > 0 ? connectedMembers : members;
        var chosen = RepresentativeSelector.Pick(pickFrom, rule);
        int siblings = members.Count - 1;

        chosenToCopy.Add((chosen, siblings));
        foreach (var skip in members.Where(m => m.Id != chosen.Id))
            plan.ToSkip.Add(new ConsolidationItem { File = skip, WillCopy = false, DuplicateSiblings = siblings });
    }

    /// <summary>
    /// 3.5: схлопывание ПОХОЖИХ фото среди файлов К КОПИРОВАНИЮ. Кандидаты — изображения с pHash;
    /// группируются с консервативным порогом <see cref="SimilarCollapseThreshold"/> через DSU.
    /// При этом пороге 64-битные pHash связаны ребром только при расстоянии Хэмминга 0 или 1,
    /// поэтому точный индекс «хэш + 64 одно-битных соседа» сохраняет семантику полного попарного
    /// сравнения (включая транзитивные цепочки), но работает за O(64*N), а не O(N²).
    /// Из каждой группы остаётся самый резкий кадр (Sharpness из 3.7; fallback — больший размер
    /// файла), остальные помечаются схлопнутыми и НЕ копируются. Возвращает прореженный список.
    /// </summary>
    private static List<(FileRecord File, int Siblings)> CollapseSimilarPhotos(
        List<(FileRecord File, int Siblings)> chosen, ConsolidationPlan plan)
    {
        var candidates = chosen
            .Where(c => c.File.Category == FileCategory.Image && c.File.PHash is { Length: >= 8 })
            .ToList();
        if (candidates.Count < 2) return chosen;

        var hashOf = candidates.ToDictionary(c => c.File.Id, c => PerceptualLsh.ToHash(c.File.PHash));
        var dsu = new Dsu();
        var representativeByHash = new Dictionary<ulong, long>();
        foreach (var candidate in candidates)
        {
            long id = candidate.File.Id;
            ulong hash = hashOf[id];
            dsu.MakeSet(id);

            // Все одинаковые хэши уже соединены через одного представителя; сравнивать каждую пару
            // внутри огромной одинаковой корзины не нужно.
            if (representativeByHash.TryGetValue(hash, out var same))
                dsu.Union(id, same);

            for (int bit = 0; bit < PerceptualLsh.TotalBits; bit++)
            {
                ulong neighbor = hash ^ (1UL << bit);
                if (representativeByHash.TryGetValue(neighbor, out var near))
                    dsu.Union(id, near);
            }

            representativeByHash.TryAdd(hash, id);
        }

        var fileById = candidates.ToDictionary(c => c.File.Id, c => c.File);
        var siblingsById = candidates.ToDictionary(c => c.File.Id, c => c.Siblings);
        var collapsedIds = new HashSet<long>();
        foreach (var (_, ids) in dsu.Groups())
        {
            if (ids.Count < 2) continue;
            var members = ids.Select(id => fileById[id]).ToList();
            var keep = PickSharpest(members);
            foreach (var m in members)
            {
                if (m.Id == keep.Id) continue;
                collapsedIds.Add(m.Id);
                plan.CollapsedSimilar.Add(new ConsolidationItem
                {
                    File = m,
                    WillCopy = false,
                    CollapsedInto = keep,
                    SimilarityToKept = PerceptualLsh.Similarity(hashOf[keep.Id], hashOf[m.Id]),
                    DuplicateSiblings = siblingsById[m.Id]
                });
            }
        }

        return collapsedIds.Count == 0 ? chosen : chosen.Where(c => !collapsedIds.Contains(c.File.Id)).ToList();
    }

    /// <summary>
    /// 3.5: какой кадр похожей группы оставить в архиве — с максимальной резкостью (3.7); резкость
    /// не посчитана ни у кого — больший размер файла; тай-брейки (размер, затем меньший id) делают
    /// план детерминированным.
    /// </summary>
    private static FileRecord PickSharpest(List<FileRecord> members)
    {
        var withSharpness = members.Where(f => f.Sharpness is not null).ToList();
        if (withSharpness.Count > 0)
        {
            return withSharpness
                .OrderByDescending(f => f.Sharpness!.Value)
                .ThenByDescending(f => f.Size)
                .ThenBy(f => f.Id)
                .First();
        }
        return members.OrderByDescending(f => f.Size).ThenBy(f => f.Id).First();
    }

    public ConsolidationReport Execute(ConsolidationPlan plan, ConsolidationOptions options,
        Action<int, int, string>? onProgress, CancellationToken ct)
    {
        var report = new ConsolidationReport();
        var catalog = _volumeService.SyncAndGetCatalog();
        var mountById = catalog
            .Where(v => v.IsConnected && v.MountPath is not null)
            .ToDictionary(v => v.Id, v => v.MountPath!);

        // Заранее проверяем доступность целевой папки на запись — даём понятную ошибку,
        // а не падаем посреди копирования.
        try
        {
            Directory.CreateDirectory(LongPath.Fix(options.TargetPath));
            var probe = Path.Combine(options.TargetPath, ".dedup_write_test");
            File.WriteAllText(LongPath.Fix(probe), string.Empty);
            File.Delete(LongPath.Fix(probe));
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Errors.Add($"Нет доступа к целевой папке «{options.TargetPath}»: {ex.Message}");
            return report;
        }

        int total = plan.ToCopy.Count;
        int done = 0;

        foreach (var item in plan.ToCopy)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (!mountById.TryGetValue(item.File.VolumeId, out var mount))
            {
                report.Failed++;
                report.Errors.Add($"Носитель отключён: {item.File.RelPath}");
                continue;
            }

            var src = Path.Combine(mount, item.File.RelPath);
            var dst = Path.Combine(options.TargetPath, item.TargetRelPath);
            onProgress?.Invoke(done, total, item.File.Name);

            try
            {
                if (!File.Exists(LongPath.Fix(src)))
                {
                    report.Failed++;
                    // mountById строился на старте: том могли выдернуть ПОСРЕДИ копирования.
                    // Пропавший корень — это «носитель отключён», а не «файл не найден».
                    report.Errors.Add(Directory.Exists(LongPath.Fix(mount))
                        ? $"Файл не найден: {src}"
                        : $"Носитель отключён: {item.File.RelPath}");
                    continue;
                }
                if (options.MoveInsteadOfCopy)
                    SafeIo.MoveSafe(src, dst, ct);
                else
                    SafeIo.CopyAtomic(src, dst, ct);

                report.Copied++;
                report.BytesCopied += item.File.Size;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Failed++;
                // Диск выдернули прямо во время копирования этого файла — сообщаем понятно,
                // а не системным текстом исключения; пакет продолжает работу.
                report.Errors.Add(Directory.Exists(LongPath.Fix(mount))
                    ? $"{item.File.Name}: {ex.Message}"
                    : $"Носитель отключён: {item.File.RelPath}");
            }
        }

        report.Skipped = plan.ToSkip.Count;

        // 3.5: схлопнутые похожие явно перечисляются в итоге — что не поехало и какой кадр оставлен.
        // Сами файлы-источники не тронуты: консолидация не удаляет ничего с исходных носителей.
        report.CollapsedSimilar = plan.CollapsedSimilar.Count;
        foreach (var c in plan.CollapsedSimilar)
        {
            var vol = c.File.VolumeLabel ?? $"том #{c.File.VolumeId}";
            report.CollapsedDetails.Add(
                $"«{c.File.RelPath}» ({vol}) схлопнут как похожее фото (сходство {c.SimilarityToKept:P1}) — " +
                $"в архив поехал «{c.CollapsedInto?.RelPath}»");
        }
        return report;
    }

    private static string UniqueTarget(FileRecord file, ConsolidationOptions options, HashSet<string> used)
    {
        string rel = options.PreserveFolderStructure
            ? Path.Combine(Sanitize(file.VolumeLabel ?? $"vol_{file.VolumeId}"), file.RelPath)
            : file.Name;

        string candidate = rel;
        int i = 1;
        while (!used.Add(candidate))
        {
            // При плоской структуре конфликт разрешаем в корне; иначе — в исходной папке.
            var dir = options.PreserveFolderStructure ? (Path.GetDirectoryName(rel) ?? string.Empty) : string.Empty;
            var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
            var ext = Path.GetExtension(file.Name);
            candidate = Path.Combine(dir, $"{nameNoExt}__{i}{ext}");
            i++;
        }
        return candidate;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
