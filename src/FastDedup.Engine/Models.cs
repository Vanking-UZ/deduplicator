namespace FastDedup.Engine;

public enum StorageType { Unknown, Ssd, Hdd }

public enum ScanPhase { Enumerating, SizeFiltered, HeadTailHashing, FullHashing, ByteCompare, Done }

/// <summary>Один файл-кандидат. CreationRaw — сырой FILETIME (меньше = старше).</summary>
public sealed record FileEntry(string Path, long Size, long CreationRaw);

/// <summary>
/// Стабильная идентичность файла на NTFS. Нужна, чтобы hardlink-алиасы не считались
/// отдельными физическими копиями и не завышали reclaimable space.
/// </summary>
public readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex, uint LinkCount);

/// <summary>
/// Файл изменился между сбором метаданных и чтением содержимого. Такой файл безопаснее
/// пропустить и взять на следующем инкрементальном скане, чем смешивать старый size с новым hash.
/// </summary>
public sealed class FileChangedDuringScanException : IOException
{
    public string Path { get; }
    public long ExpectedSize { get; }
    public long ActualSize { get; }

    public FileChangedDuringScanException(string path, long expectedSize, long actualSize)
        : base($"Файл изменился во время сканирования: ожидался размер {expectedSize}, фактический {actualSize}: {path}")
    {
        Path = path;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }
}

public sealed record ScanError(string Path, string Message);

public sealed record ScanProgress(ScanPhase Phase, long Count);

public sealed record ScanStats(
    long FilesSeen,
    long AfterSizeFilter,
    long AfterHeadTail,
    long FullHashed,
    long BytesRead,
    TimeSpan Elapsed,
    long ReclaimableBytes,
    long HardLinkAliasesSkipped,
    long FilesChangedSkipped);

public sealed class ScanOptions
{
    /// <summary>Корни сканирования. Могут быть на разных томах.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Сколько байт головы и хвоста читать на первом проходе хэширования.</summary>
    public int HeadTailChunkBytes { get; init; } = 64 * 1024;

    /// <summary>Минимальный размер файла. По умолчанию 1: пустые файлы не дают экономии места.</summary>
    public long MinFileSize { get; init; } = 1;

    /// <summary>Максимальный размер файла. null — без ограничения.</summary>
    public long? MaxFileSize { get; init; }

    /// <summary>Идти ли внутрь reparse-точек (симлинки/джанкшены). По умолчанию нет — защита от циклов.</summary>
    public bool IncludeReparsePoints { get; init; } = false;

    /// <summary>
    /// Колбэк «пропущен reparse/облачный элемент» (путь, каталог ли). Плейсхолдеры OneDrive —
    /// reparse-точки даже в скачанном состоянии, и раньше целая библиотека молча выпадала из
    /// скана без какого-либо следа: пользователь получал честное «дубликатов нет» по
    /// непроверенным файлам. Счётчик выводится в UI как предупреждение.
    /// </summary>
    public Action<string, bool>? OnReparseSkipped { get; init; }

    /// <summary>Пропускать скрытые/system-файлы и каталоги. По умолчанию false: режим «без потерь» видит всё доступное.</summary>
    public bool SkipHiddenSystem { get; init; } = false;

    /// <summary>Расширения, которые надо исключить: например .tmp, .cache. Сравнение без учёта регистра.</summary>
    public IReadOnlyCollection<string> ExcludedExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Имена каталогов, чьи поддеревья надо исключить: например node_modules, .git.</summary>
    public IReadOnlyCollection<string> ExcludedDirectoryNames { get; init; } = Array.Empty<string>();

    /// <summary>Принудительно задать тип носителя, минуя авто-детекцию (для тестов/особых случаев).</summary>
    public StorageType? ForceStorageType { get; init; }

    /// <summary>Переопределить параллелизм этапа сбора метаданных.</summary>
    public int? MaxStatParallelism { get; init; }

    /// <summary>Переопределить параллелизм этапов чтения содержимого.</summary>
    public int? MaxContentParallelism { get; init; }

    /// <summary>Ёмкость bounded-channel между перечислением и metadata-stage.</summary>
    public int? ChannelCapacity { get; init; }

    /// <summary>Сколько томов можно хэшировать одновременно. null — авто.</summary>
    public int? MaxVolumeParallelism { get; init; }

    /// <summary>
    /// true — показывать hardlink-алиасы как дубликаты. false — схлопывать одинаковый file-id,
    /// чтобы не обещать экономию места там, где несколько имён указывают на один физический файл.
    /// </summary>
    public bool IncludeHardLinkAliases { get; init; } = false;

    /// <summary>
    /// Проверять, что размер файла не изменился между metadata-stage и чтением содержимого.
    /// Это медленно почти не влияет, зато убирает race-condition с активно пишущимися файлами.
    /// </summary>
    public bool VerifyStableSize { get; init; } = true;
}

public sealed class DuplicateGroup
{
    public long Size { get; }
    public IReadOnlyList<string> Files { get; }
    public string SuggestedOriginal { get; }

    /// <summary>Сколько освободится, если оставить одну копию.</summary>
    public long ReclaimableBytes => Size * (Files.Count - 1);

    private DuplicateGroup(long size, IReadOnlyList<string> files, string original)
    {
        Size = size; Files = files; SuggestedOriginal = original;
    }

    public static DuplicateGroup From(IReadOnlyList<FileEntry> cluster, long size)
    {
        // «Оригиналом» предлагаем самый старый по дате создания (нулевую дату прячем в конец).
        var original = cluster
            .OrderBy(f => f.CreationRaw == 0 ? long.MaxValue : f.CreationRaw)
            .First().Path;
        return new DuplicateGroup(size, cluster.Select(f => f.Path).ToList(), original);
    }
}

public sealed record ScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyList<ScanError> Errors,
    ScanStats Stats,
    IReadOnlyDictionary<string, StorageType> VolumeStorage);
