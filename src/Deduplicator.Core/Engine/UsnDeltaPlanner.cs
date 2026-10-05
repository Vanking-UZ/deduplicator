using Fde = FastDedup.Engine;

namespace Deduplicator.Core.Engine;

/// <summary>Флаги Reason записей USN-журнала (winioctl.h, USN_REASON_*), нужные классификации.</summary>
public static class UsnReason
{
    public const uint DataOverwrite = 0x00000001;
    public const uint DataExtend = 0x00000002;
    public const uint DataTruncation = 0x00000004;
    public const uint NamedDataOverwrite = 0x00000010;
    public const uint NamedDataExtend = 0x00000020;
    public const uint NamedDataTruncation = 0x00000040;
    public const uint FileCreate = 0x00000100;
    public const uint FileDelete = 0x00000200;
    public const uint EaChange = 0x00000400;
    public const uint SecurityChange = 0x00000800;
    public const uint RenameOldName = 0x00001000;
    public const uint RenameNewName = 0x00002000;
    public const uint IndexableChange = 0x00004000;
    public const uint BasicInfoChange = 0x00008000;
    public const uint HardLinkChange = 0x00010000;
    public const uint CompressionChange = 0x00020000;
    public const uint EncryptionChange = 0x00040000;
    public const uint ObjectIdChange = 0x00080000;
    public const uint ReparsePointChange = 0x00100000;
    public const uint StreamChange = 0x00200000;
    public const uint Close = 0x80000000;
}

/// <summary>Действие реконсайла для одной записи дельты.</summary>
public enum UsnDeltaAction
{
    /// <summary>Запись не влияет на индекс (например, создание каталога).</summary>
    Ignore,
    /// <summary>Имя исчезло из этого пути (удаление/rename) — удалить строку files по пути.</summary>
    DeleteRow,
    /// <summary>Файл менялся — пере-stat с диска и переиндексация обычным путём.</summary>
    Restat,
    /// <summary>Каталожная rename/delete-операция — дельта небезопасна, полный пересбор тома.</summary>
    DirectoryAnomaly
}

/// <summary>
/// План применения дельты: пути к удалению строк, пути к пере-stat, либо причина аномалии
/// (не null → применять НИЧЕГО нельзя, только полный пересбор тома).
/// </summary>
public sealed class UsnDeltaPlan
{
    public string? AnomalyReason { get; init; }
    public IReadOnlyList<string> Deletes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<UsnDeltaRestat> Restats { get; init; } = Array.Empty<UsnDeltaRestat>();
}

/// <summary>
/// Файл, который нужно повторно прочитать с диска. Reason сохраняется до индексатора: одних
/// метаданных недостаточно, чтобы решить, можно ли переиспользовать старые хэши.
/// </summary>
public readonly record struct UsnDeltaRestat(string FullPath, uint Reason);

/// <summary>
/// Чистая классификация записей дельты USN → действия реконсайла. Ни диска, ни БД, ни нативных
/// вызовов — полностью покрывается юнит-тестами (живой журнал в тестах недоступен без админа).
///
/// Принцип безопасности: сомнительное действие не «улучшается», а превращается в аномалию
/// (→ полный пересбор). Единственные точечные операции — удаление строки по точному пути и
/// пере-stat файла, где источником истины служит сам диск.
/// </summary>
public static class UsnDeltaPlanner
{
    // Только эти причины однозначно не меняют содержимое основного/именованного потока файла.
    // Любой другой или неизвестный бит трактуется консервативно: старые хэши сбрасываются.
    private const uint MetadataOnlyReasonMask =
        UsnReason.EaChange |
        UsnReason.SecurityChange |
        UsnReason.IndexableChange |
        UsnReason.BasicInfoChange |
        UsnReason.ObjectIdChange |
        UsnReason.Close;

    /// <summary>
    /// Нужно ли обязательно инвалидировать хэши при re-stat. Data*/NamedData*, StreamChange и
    /// HardLinkChange попадают сюда напрямую; create/rename-new, reparse/compression/encryption,
    /// нулевая маска и неизвестные флаги — как неоднозначные случаи.
    /// </summary>
    public static bool RequiresHashInvalidation(uint reason) =>
        reason == 0 || (reason & ~MetadataOnlyReasonMask) != 0;

    /// <summary>Классификация одной записи по Reason-маске и признаку каталога.</summary>
    public static UsnDeltaAction Classify(uint reason, bool isDirectory)
    {
        if (isDirectory)
        {
            // Rename/delete КАТАЛОГА меняет пути всех вложенных файлов, а их записей в дельте нет —
            // точечный реконсайл потерял бы поддерево. Честно и безопасно: полный пересбор тома.
            return (reason & (UsnReason.FileDelete | UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0
                ? UsnDeltaAction.DirectoryAnomaly
                : UsnDeltaAction.Ignore; // создание каталога/смена его атрибутов строк files не трогает
        }

        // Имя исчезло из этого пути: файл удалён или уехал (rename) — строку по СТАРОМУ пути удалить.
        if ((reason & (UsnReason.FileDelete | UsnReason.RenameOldName)) != 0)
            return UsnDeltaAction.DeleteRow;

        // Всё остальное (create / data* / rename-new / basic-info / … и даже чистый Close) —
        // пере-stat с диска: диск — источник истины, лишний stat дёшев и не теряет изменение.
        return UsnDeltaAction.Restat;
    }

    /// <summary>
    /// Сборка плана: группировка записей по ТОЧНОМУ (Ordinal) пути + слияние действий.
    /// Ordinal, а не IgnoreCase: rel_path в БД сравнивается байтово, и при смене только регистра
    /// (rename foo→FOO) должны примениться ОБА действия — Delete(старый) и Restat(новый).
    /// Слияние: Restat главнее DeleteRow НЕЗАВИСИМО от порядка записей — судьбу пути решает диск
    /// (удалён→создан заново: restat найдёт файл; создан→удалён: stat промахнётся, строка удалится),
    /// поэтому результат не зависит от порядка записей в журнале.
    /// </summary>
    public static UsnDeltaPlan Build(IReadOnlyList<Fde.UsnDeltaChange> changes)
    {
        var byPath = new Dictionary<string, (UsnDeltaAction Action, uint Reason)>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            if (string.IsNullOrWhiteSpace(c.FullPath))
                return new UsnDeltaPlan { AnomalyReason = "запись дельты с пустым путём" };

            var action = Classify(c.Reason, c.IsDirectory);
            if (action == UsnDeltaAction.DirectoryAnomaly)
                return new UsnDeltaPlan { AnomalyReason = $"каталожная операция rename/delete: {c.FullPath}" };
            if (action == UsnDeltaAction.Ignore) continue;

            if (byPath.TryGetValue(c.FullPath, out var prev))
            {
                // Restat главнее DeleteRow, а причины на одном пути накапливаются до Close.
                var mergedAction = prev.Action == UsnDeltaAction.Restat || action == UsnDeltaAction.Restat
                    ? UsnDeltaAction.Restat
                    : UsnDeltaAction.DeleteRow;
                byPath[c.FullPath] = (mergedAction, prev.Reason | c.Reason);
            }
            else
            {
                byPath[c.FullPath] = (action, c.Reason);
            }
        }

        var deletes = new List<string>();
        var restats = new List<UsnDeltaRestat>();
        foreach (var (path, entry) in byPath)
        {
            if (entry.Action == UsnDeltaAction.DeleteRow) deletes.Add(path);
            else restats.Add(new UsnDeltaRestat(path, entry.Reason));
        }

        return new UsnDeltaPlan { Deletes = deletes, Restats = restats };
    }
}
