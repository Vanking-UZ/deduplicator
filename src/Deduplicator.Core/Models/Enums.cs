namespace Deduplicator.Core.Models;

/// <summary>Категория файла, определяемая по магическим байтам содержимого, а не по расширению.</summary>
public enum FileCategory
{
    Other = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    Document = 4
}

/// <summary>Этап обработки конкретного файла в многоступенчатой воронке.</summary>
public enum ScanState
{
    /// <summary>Собраны только метаданные (путь, размер, дата, категория).</summary>
    Meta = 0,
    /// <summary>Посчитан частичный хэш (голова + хвост).</summary>
    Partial = 1,
    /// <summary>Посчитан полный хэш содержимого.</summary>
    Full = 2,
    /// <summary>Посчитаны перцептивные признаки (pHash / аудио-отпечаток).</summary>
    Perceptual = 3
}

/// <summary>Тип совпадения в группе дубликатов.</summary>
public enum MatchType
{
    Exact = 0,
    SimilarImage = 1,
    SimilarAudio = 2,
    ExactVideo = 3
}

/// <summary>Глубина сверки, выбираемая пользователем перед запуском.</summary>
public enum ScanDepth
{
    /// <summary>Только точные копии (для всех типов файлов).</summary>
    ExactOnly = 0,
    /// <summary>Точные копии + похожие изображения.</summary>
    WithSimilarImages = 1,
    /// <summary>Точные копии + похожие изображения + похожая музыка.</summary>
    WithSimilarImagesAndAudio = 2
}

/// <summary>Крупный этап пайплайна — для отображения прогресса.</summary>
public enum ScanStage
{
    Idle = 0,
    Enumerating = 1,
    PartialHashing = 2,
    FullHashing = 3,
    ByteVerification = 4,
    Perceptual = 5,
    Grouping = 6,
    Completed = 7,
    Cancelled = 8,
    Failed = 9
}

/// <summary>Тип операции в журнале (обратимость).</summary>
public enum OperationType
{
    DeleteToRecycle = 0,
    MoveToQuarantine = 1,
    Hardlink = 2,
    Restore = 3
}

/// <summary>Правило выбора эталона (какую копию оставить / перенести).</summary>
public enum RepresentativeRule
{
    /// <summary>Самый старый по дате изменения.</summary>
    Oldest = 0,
    /// <summary>Самый новый по дате изменения.</summary>
    Newest = 1,
    /// <summary>С самым коротким путём (ближе к корню — «главная» папка).</summary>
    ShortestPath = 2,
    /// <summary>С самым длинным именем (как правило, более осмысленным).</summary>
    LongestName = 3,
    /// <summary>Умный скоринг (2.10): папки-эталоны, «мусорные» папки, имена-копии, дата создания.
    /// Только предвыбор «что оставить» — политику удаления не меняет. См. KeepScorer.</summary>
    Smart = 4
}
