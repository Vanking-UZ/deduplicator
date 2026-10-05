namespace Deduplicator.Core.Models;

/// <summary>Как комбинировать условия профиля похожести.</summary>
public enum SimilarityCombineMode
{
    /// <summary>Все условия должны выполняться (по умолчанию).</summary>
    AllConditions = 0,
    /// <summary>Достаточно любого условия.</summary>
    AnyCondition = 1,
    /// <summary>Сначала похожесть (порог), затем фильтры.</summary>
    SimilarityThenFilters = 2
}

/// <summary>
/// Профиль анализа похожести (P0-C): порог + дата создания/изменения + размер + папки + расширения.
/// Хранится как JSON. Применяется как ПРЕДфильтр кандидатов до тяжёлых перцептивных операций.
/// </summary>
public sealed class SimilarityProfile
{
    public string Name { get; set; } = "Обычно";

    public HashSet<FileCategory> Categories { get; set; } = Enum.GetValues<FileCategory>().ToHashSet();

    public double? ImageThreshold { get; set; }
    public double? AudioThreshold { get; set; }

    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }

    public DateTimeOffset? CreatedFrom { get; set; }
    public DateTimeOffset? CreatedTo { get; set; }
    public DateTimeOffset? ModifiedFrom { get; set; }
    public DateTimeOffset? ModifiedTo { get; set; }

    /// <summary>Если задано — оставляем только эти расширения (с точкой, без регистра).</summary>
    public List<string> IncludeExtensions { get; set; } = new();
    public List<string> ExcludeExtensions { get; set; } = new();

    /// <summary>Если задано — путь должен начинаться с одной из этих папок.</summary>
    public List<string> IncludeFolders { get; set; } = new();
    public List<string> ExcludeFolders { get; set; } = new();

    public SimilarityCombineMode CombineMode { get; set; } = SimilarityCombineMode.AllConditions;
}
