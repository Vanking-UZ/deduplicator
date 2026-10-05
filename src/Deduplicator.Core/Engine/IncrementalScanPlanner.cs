using Deduplicator.Core.Models;

namespace Deduplicator.Core.Engine;

public enum ScanPlanKind
{
    /// <summary>Полный пересбор индекса тома (первый скан, сброс журнала, смена параметров и т.п.).</summary>
    FullRescan,
    /// <summary>Том не менялся с прошлого скана — перечисление можно пропустить, индекс актуален.</summary>
    Unchanged,
    /// <summary>Есть изменения — читать их из журнала USN с сохранённого USN.</summary>
    Incremental
}

public readonly record struct IncrementalDecision(ScanPlanKind Kind, string Reason);

/// <summary>
/// Чистое решение об инкрементальности по сохранённому и текущему состоянию USN-журнала тома.
/// Никаких побочных эффектов и нативных вызовов — поэтому полностью покрывается юнит-тестами.
///
/// Безопасность важнее скорости: при любой неоднозначности — полный пересбор (данные не теряются,
/// просто медленнее). Нативное чтение журнала (которое нельзя проверить без админа/реального NTFS)
/// при любом сбое отдаёт «журнал недоступен», и планировщик честно выбирает полный пересбор.
/// </summary>
public static class IncrementalScanPlanner
{
    /// <summary>Версия формата индекса. Увеличивать при изменениях, ломающих переиспользование индекса.</summary>
    public const int EngineVersion = 1;

    public static IncrementalDecision Decide(
        VolumeScanState? saved,
        bool journalAvailable, long currentJournalId, long currentFirstUsn, long currentNextUsn,
        int engineVersion, string optionsHash)
    {
        if (!journalAvailable)
            return new(ScanPlanKind.FullRescan, "журнал USN недоступен (нет прав/не NTFS/нет журнала)");
        if (saved is null)
            return new(ScanPlanKind.FullRescan, "первый скан тома");
        if (saved.EngineVersion != engineVersion)
            return new(ScanPlanKind.FullRescan, "версия движка изменилась");
        if (!string.Equals(saved.OptionsHash, optionsHash, StringComparison.Ordinal))
            return new(ScanPlanKind.FullRescan, "параметры скана изменились");
        if (saved.JournalId != currentJournalId)
            return new(ScanPlanKind.FullRescan, "журнал USN пересоздан");
        if (saved.NextUsn < currentFirstUsn)
            return new(ScanPlanKind.FullRescan, "журнал USN усечён — часть изменений потеряна");
        if (saved.NextUsn > currentNextUsn)
            return new(ScanPlanKind.FullRescan, "сохранённый USN из будущего — несогласованность");
        if (saved.NextUsn == currentNextUsn)
            return new(ScanPlanKind.Unchanged, "том не менялся с прошлого скана");
        return new(ScanPlanKind.Incremental, "читаем изменения с сохранённого USN");
    }
}
