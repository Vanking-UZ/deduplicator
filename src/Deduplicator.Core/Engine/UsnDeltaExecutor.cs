using Fde = FastDedup.Engine;

namespace Deduplicator.Core.Engine;

/// <summary>
/// Крючки применения дельты. Отделяют ПОРЯДОК применения (этот файл, чистая логика) от
/// ScanService/БД/диска: применение прогоняется тестами на фейковой дельте без Windows-журнала.
/// </summary>
public sealed class UsnDeltaApplyHooks
{
    /// <summary>Метаданные файла с диска; null — файла нет (или носитель отключился, см. VolumeAlive).</summary>
    public required Func<string, Fde.EnumeratedFile?> Stat { get; init; }

    /// <summary>
    /// Проиндексировать файл ТЕМ ЖЕ путём, что при обычном перечислении (фильтры атрибутов/
    /// системных папок/категорий + строгое переиспользование метаданных 2.7 + UpsertMetaBatch).
    /// Второй аргумент — объединённая USN Reason-маска этого пути; индексатор использует её,
    /// чтобы изменения содержимого не прошли как «неизменённые» только по size/mtime/created.
    /// false — файл отфильтрован и в индексе ему не место.
    /// </summary>
    public required Func<Fde.EnumeratedFile, uint, bool> TryIndexFile { get; init; }

    /// <summary>Удалить строку files по rel_path (том фиксирован вызывающим). Ноль строк — норма.</summary>
    public required Action<string> DeleteByRelPath { get; init; }

    /// <summary>Жив ли том — защита от «носитель выдернули посреди применения» (см. Apply).</summary>
    public required Func<bool> VolumeAlive { get; init; }
}

/// <summary>Итог применения дельты. AnomalyReason == null — успех.</summary>
public sealed record UsnDeltaApplyOutcome(string? AnomalyReason, int Reindexed, int Deleted)
{
    public bool Success => AnomalyReason is null;
}

/// <summary>
/// Применение плана дельты к индексу. Гарантия «дельта не теряет файлы»:
///  • все пути валидируются ДО первого изменения БД (путь вне тома → аномалия, ничего не тронуто);
///  • судьбу каждого изменённого пути решает ДИСК (restat), а не догадка по Reason-маске;
///  • промах stat при мёртвом томе — аномалия, а не «файл удалён» (строки не выкашиваются);
///  • любой сбой посреди применения → аномалия → вызывающий делает полный пересбор, который
///    заново перестраивает все строки (уже применённые удаления безопасны: они относились к путям,
///    изменение которых журнал зафиксировал достоверно, и upsert полного пересбора их вернёт).
/// </summary>
public static class UsnDeltaExecutor
{
    public static UsnDeltaApplyOutcome Apply(UsnDeltaPlan plan, string mountRoot, UsnDeltaApplyHooks hooks)
    {
        if (plan.AnomalyReason is not null)
            return new(plan.AnomalyReason, 0, 0);

        // Фаза 0: валидация ВСЕХ путей до каких-либо изменений БД.
        var deleteRel = new List<string>(plan.Deletes.Count);
        var restatRel = new List<(string Full, string Rel, uint Reason)>(plan.Restats.Count);
        foreach (var full in plan.Deletes)
        {
            if (!TryRel(mountRoot, full, out var rel)) return new($"путь дельты вне тома: {full}", 0, 0);
            deleteRel.Add(rel);
        }
        foreach (var restat in plan.Restats)
        {
            if (!TryRel(mountRoot, restat.FullPath, out var rel))
                return new($"путь дельты вне тома: {restat.FullPath}", 0, 0);
            restatRel.Add((restat.FullPath, rel, restat.Reason));
        }
        if (!hooks.VolumeAlive())
            return new("носитель недоступен — дельта не применяется", 0, 0);

        int deleted = 0, reindexed = 0;
        try
        {
            foreach (var rel in deleteRel)
            {
                hooks.DeleteByRelPath(rel);
                deleted++;
            }

            foreach (var (full, rel, reason) in restatRel)
            {
                var ef = hooks.Stat(full);
                if (ef is null)
                {
                    // Файла нет. Если исчез ВЕСЬ том — это не «файл удалён», а аномалия носителя:
                    // остановиться, не выкашивая строки (деградация безопасна — полный пересбор).
                    if (!hooks.VolumeAlive())
                        return new("носитель отключился во время применения дельты", reindexed, deleted);
                    hooks.DeleteByRelPath(rel);
                    deleted++;
                    continue;
                }

                if (hooks.TryIndexFile(ef.Value, reason))
                {
                    reindexed++;
                }
                else
                {
                    // Файл существует, но отфильтрован (скрытый/системная папка/категория/reparse/
                    // каталог). В полном скане с прунингом его строки бы не осталось; держать её со
                    // СТАРЫМИ хэшами при изменённом содержимом опасно (устаревший хэш в воронке) —
                    // строку удаляем. Классика: удаление в Корзину = rename в $Recycle.Bin.
                    hooks.DeleteByRelPath(rel);
                    deleted++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // отмена/пауза скана — не аномалия дельты
        }
        catch (VolumeIdentityChangedException)
        {
            // Это не повод пробовать full-rescan по той же букве: там уже другой физический том.
            // Ошибка обязана дойти до ScanService и завершить всю сессию без публикации/checkpoint.
            throw;
        }
        catch (Exception ex)
        {
            return new("сбой применения дельты: " + ex.Message, reindexed, deleted);
        }

        return new(null, reindexed, deleted);
    }

    /// <summary>Относительный путь внутри тома; false — путь корня, вне тома или неразбираемый.</summary>
    private static bool TryRel(string mountRoot, string fullPath, out string rel)
    {
        rel = string.Empty;
        try
        {
            rel = Path.GetRelativePath(mountRoot, fullPath);
            if (rel.Length == 0 || rel == "." || rel == ".." || Path.IsPathRooted(rel)) return false;
            if (rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
