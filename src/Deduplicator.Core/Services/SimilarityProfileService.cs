using System.Text.Json;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>
/// Профили похожести (P0-C): пресеты для простого режима, чистый предфильтр кандидатов (размер/дата/
/// расширение/папка) и JSON-персистентность. Предфильтр применяется ДО перцептивных операций, чтобы не
/// считать признаки для заведомо неподходящих файлов.
/// </summary>
public sealed class SimilarityProfileService
{
    private const int MaxProfilesBytes = 4 * 1024 * 1024;
    private readonly string _path;

    public SimilarityProfileService(string? storePath = null)
        => _path = storePath ?? "similarity-profiles.json";

    // --- Пресеты для простого режима ---

    public static SimilarityProfile Strict() => new()
    {
        Name = "Строго", ImageThreshold = 0.97, AudioThreshold = 0.95,
        CombineMode = SimilarityCombineMode.SimilarityThenFilters
    };

    public static SimilarityProfile Normal() => new()
    {
        Name = "Обычно", ImageThreshold = 0.90, AudioThreshold = 0.85,
        CombineMode = SimilarityCombineMode.SimilarityThenFilters
    };

    public static SimilarityProfile Wide() => new()
    {
        Name = "Шире", ImageThreshold = 0.80, AudioThreshold = 0.75,
        CombineMode = SimilarityCombineMode.SimilarityThenFilters
    };

    public static IReadOnlyList<SimilarityProfile> Presets() => new[] { Strict(), Normal(), Wide() };

    /// <summary>
    /// Подходит ли файл под фильтры профиля (без учёта самой похожести — это отдельный порог).
    /// Чистая функция, легко тестируется. <paramref name="relPathOrFull"/> используется для папок/расширения.
    /// </summary>
    public static bool Matches(FileCategory category, long size, DateTimeOffset created, DateTimeOffset modified,
        string relPathOrFull, SimilarityProfile p)
    {
        if (p.Categories.Count > 0 && !p.Categories.Contains(category)) return false;

        if (p.MinSize is long min && size < min) return false;
        if (p.MaxSize is long max && max > 0 && size > max) return false;

        if (p.CreatedFrom is { } createdFrom && created < createdFrom) return false;
        if (p.CreatedTo is { } createdTo && created > createdTo) return false;
        if (p.ModifiedFrom is { } modFrom && modified < modFrom) return false;
        if (p.ModifiedTo is { } modTo && modified > modTo) return false;

        var ext = Path.GetExtension(relPathOrFull);
        if (p.IncludeExtensions.Count > 0 && !p.IncludeExtensions.Any(e => EqExt(e, ext))) return false;
        if (p.ExcludeExtensions.Any(e => EqExt(e, ext))) return false;

        var norm = relPathOrFull.Replace('/', '\\');
        if (p.IncludeFolders.Count > 0 &&
            !p.IncludeFolders.Any(f => norm.StartsWith(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)))
            return false;
        if (p.ExcludeFolders.Any(f => norm.StartsWith(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static bool EqExt(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        if (a[0] != '.') a = "." + a;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // --- Персистентность (JSON) ---

    public IReadOnlyList<SimilarityProfile> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<SimilarityProfile>();
            var json = SafeIo.ReadAllTextLimited(_path, MaxProfilesBytes);
            return JsonSerializer.Deserialize<List<SimilarityProfile>>(json) ?? new List<SimilarityProfile>();
        }
        catch { return Array.Empty<SimilarityProfile>(); }
    }

    public void Save(IReadOnlyList<SimilarityProfile> profiles)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SafeIo.WriteAllTextAtomic(_path, JsonSerializer.Serialize(profiles,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* профили — не критичны для работы */ }
    }
}
