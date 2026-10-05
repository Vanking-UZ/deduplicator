namespace Deduplicator.Core.Models;

/// <summary>Параметры запуска сверки.</summary>
public sealed class ScanOptions
{
    /// <summary>Тома, выбранные для сканирования.</summary>
    public List<long> VolumeIds { get; set; } = new();

    /// <summary>Опционально — конкретные папки (полные пути) вместо целого тома.</summary>
    public List<string> Folders { get; set; } = new();

    public ScanDepth Depth { get; set; } = ScanDepth.ExactOnly;

    /// <summary>Степень параллелизма хэширования (для SSD &gt; 1, для HDD = 1).</summary>
    public int Parallelism { get; set; } = 1;

    /// <summary>Переиспользовать сохранённые хэши для неизменившихся файлов.</summary>
    public bool Incremental { get; set; } = true;

    /// <summary>Порог схожести изображений (0..1), напр. 0.90.</summary>
    public double ImageThreshold { get; set; } = 0.90;

    /// <summary>Порог схожести аудио (0..1).</summary>
    public double AudioThreshold { get; set; } = 0.85;

    // --- Фильтры сканирования ---

    /// <summary>Минимальный размер файла (байты); меньшие пропускаются.</summary>
    public long MinFileSize { get; set; } = 0;

    /// <summary>Максимальный размер файла (байты); 0/нет — без ограничения.</summary>
    public long? MaxFileSize { get; set; }

    /// <summary>Расширения (с точкой, в нижнем регистре), которые пропускать.</summary>
    public HashSet<string> ExcludedExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пропускать скрытые и системные файлы/папки.</summary>
    public bool SkipHiddenSystem { get; set; } = true;

    /// <summary>
    /// Какие категории файлов обрабатывать (P0-A). По умолчанию — все. Пустой набор — ошибка валидации
    /// («не выбран ни один тип»). Файлы вне набора не индексируются → exact/похожие строятся только по
    /// выбранным типам.
    /// </summary>
    public HashSet<FileCategory> IncludedCategories { get; set; } =
        Enum.GetValues<FileCategory>().ToHashSet();

    /// <summary>Все ли категории включены (тогда можно безопасно прунить весь том).</summary>
    public bool AllCategoriesIncluded => IncludedCategories.Count >= Enum.GetValues<FileCategory>().Length;

    /// <summary>
    /// Профиль похожести для фото (P1.5): задаёт порог + предфильтр по размеру/дате/папке. Применяется
    /// ДО расчёта перцептивных признаков (не считаем pHash для заведомо неподходящих). null — по умолчанию.
    /// </summary>
    public SimilarityProfile? ImageProfile { get; set; }

    /// <summary>Профиль похожести для аудио (аналогично <see cref="ImageProfile"/>).</summary>
    public SimilarityProfile? AudioProfile { get; set; }
}

/// <summary>Снимок прогресса сканирования для UI (потокобезопасно копируется).</summary>
public sealed class ScanProgress
{
    public ScanStage Stage { get; set; } = ScanStage.Idle;

    /// <summary>
    /// Совместимое поле 0..1. Начиная с Engine v2 это общий монотонный прогресс всего скана
    /// (зеркало <see cref="OverallFraction"/>), а не локальный процент текущего этапа.
    /// </summary>
    public double? Fraction { get; set; }

    /// <summary>Общий монотонный прогресс всего скана, 0..1; null, пока total ещё нельзя честно оценить.</summary>
    public double? OverallFraction { get; set; }

    /// <summary>Активное время скана; время на паузе не учитывается.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Оценка оставшегося активного времени; null, пока данных недостаточно.</summary>
    public TimeSpan? EstimatedRemaining { get; set; }

    /// <summary>Сглаженная скорость реально прочитанных байтов в секунду.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>Сглаженная скорость обработанных файловых операций в секунду.</summary>
    public double FilesPerSecond { get; set; }

    /// <summary>Оценка общего числа файлов; может уточняться во время первого обхода.</summary>
    public long? EstimatedTotalFiles { get; set; }

    public string? CurrentFile { get; set; }

    public long FilesSeen { get; set; }
    public long FilesHashed { get; set; }
    public long BytesProcessed { get; set; }
    public long GroupsFound { get; set; }

    /// <summary>Количество пропущенных файлов (ошибки доступа, битые и т.п.).</summary>
    public long FilesSkipped { get; set; }

    /// <summary>Сколько файлов изменились во время сверки и были отложены до следующего скана (подмножество Skipped).</summary>
    public long FilesChangedSkipped { get; set; }

    public string? Message { get; set; }

    public ScanProgress Clone() => (ScanProgress)MemberwiseClone();

    public string StageTitle => Stage switch
    {
        ScanStage.Idle => "Ожидание",
        ScanStage.Enumerating => "Обход файловой системы",
        ScanStage.PartialHashing => "Частичный хэш",
        ScanStage.FullHashing => "Полный хэш",
        ScanStage.ByteVerification => "Побайтовая сверка",
        ScanStage.Perceptual => "Анализ похожих",
        ScanStage.Grouping => "Формирование групп",
        ScanStage.Completed => "Завершено",
        ScanStage.Cancelled => "Отменено",
        ScanStage.Failed => "Ошибка",
        _ => "—"
    };
}

/// <summary>Сохранённое в БД состояние выполняемой/прерванной сверки (возобновляемость).</summary>
public sealed class ScanSession
{
    public long Id { get; set; }
    public string OptionsJson { get; set; } = "{}";
    public ScanStage Stage { get; set; } = ScanStage.Idle;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long FilesSeen { get; set; }
    public long FilesSkipped { get; set; }
}
