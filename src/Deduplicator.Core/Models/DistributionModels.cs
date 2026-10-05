namespace Deduplicator.Core.Models;

/// <summary>Режим распределения результатов. Для MVP только безопасные: отчёт и копирование.</summary>
public enum DistributionMode
{
    /// <summary>Только отчёт CSV — на диске ничего не двигаем.</summary>
    ReportOnly = 0,
    /// <summary>Копировать файлы в папки по плану (оригиналы остаются на месте).</summary>
    CopyFiles = 1
    // MoveFiles / CreateShortcuts — НЕ для MVP (опасно/требует доп. подтверждения).
}

/// <summary>Файл-кандидат на распределение (уже разрешённый путь + параметры для раскладки).</summary>
public sealed record DistributionFile(long Id, string FullPath, string Category, double Similarity, long Size);

public sealed record DistributionRequest(
    IReadOnlyList<DistributionFile> Files,
    string OutputRoot,
    DistributionMode Mode,
    bool VerifyAfterCopy = true);

/// <summary>Строка плана: исходник → целевой путь в папке-раскладке.</summary>
public sealed record DistributionItem(
    long Id, string SourcePath, string TargetPath, string FolderKey, bool Skipped, string? Reason);

public sealed record DistributionPlan(
    IReadOnlyList<DistributionItem> Items, bool CanExecute);

/// <summary>Unavailable — источник исчез к моменту копирования (файл удалён или носитель отключён):
/// UI сообщает об этом отдельно от прочих ошибок, чтобы выдернутый диск не выглядел «сбоем».</summary>
public sealed record DistributionResult(
    int Copied, int Skipped, int Failed, string? ReportPath, string? Error, int Unavailable = 0);
