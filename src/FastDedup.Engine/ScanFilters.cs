namespace FastDedup.Engine;

/// <summary>
/// Предкомпилированные фильтры для горячего metadata-этапа: списки расширений/каталогов
/// разбираются ОДИН раз, а не на каждый файл (как в <see cref="ScanFilters"/>). Это убирает
/// миллионы аллокаций (Split + нормализация) при сканировании больших томов.
/// </summary>
internal sealed class CompiledScanFilters
{
    private readonly HashSet<string> _ext = new(StringComparer.OrdinalIgnoreCase);   // ".tmp"
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);  // "node_modules"
    private readonly bool _hasExt, _hasDirs;
    private readonly bool _includeReparse, _skipHiddenSystem;
    private readonly long _minSize;
    private readonly long? _maxSize;

    public CompiledScanFilters(ScanOptions o)
    {
        foreach (var raw in o.ExcludedExtensions)
        { var e = raw.Trim(); if (e.Length == 0) continue; if (e[0] != '.') e = "." + e; _ext.Add(e); }
        foreach (var raw in o.ExcludedDirectoryNames)
        { var d = raw.Trim(); if (d.Length != 0) _dirs.Add(d); }
        _hasExt = _ext.Count > 0; _hasDirs = _dirs.Count > 0;
        _includeReparse = o.IncludeReparsePoints;
        _skipHiddenSystem = o.SkipHiddenSystem;
        _minSize = o.MinFileSize;
        _maxSize = o.MaxFileSize;
    }

    /// <summary>Полная проверка одного файла на metadata-этапе (без аллокаций в общем случае).</summary>
    public bool ShouldSkip(string path, long size, uint attrs)
    {
        if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) return true;
        if (!_includeReparse && (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return true;
        if (_skipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) return true;
        if (size < _minSize) return true;
        if (_maxSize is long max && max >= 0 && size > max) return true;
        if (_hasExt && _ext.Contains(Path.GetExtension(path))) return true;
        if (HasExcludedDirSegment(path)) return true;
        return false;
    }

    private bool HasExcludedDirSegment(string path)
    {
        if (!_hasDirs) return false;                  // общий случай — мгновенно, без аллокаций
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == '\\' || path[i] == '/')
            {
                int len = i - start;
                // string-аллокация только когда фильтр каталогов реально задан
                if (len > 0 && path[i - 1] != ':' && _dirs.Contains(path.Substring(start, len)))
                    return true;
                start = i + 1;
            }
        }
        return false;
    }
}

internal static class ScanFilters
{
    public static bool ShouldSkipMetadata(string path, long size, uint attrs, ScanOptions options)
    {
        if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) return true;
        if (!options.IncludeReparsePoints && (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return true;
        if (options.SkipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) return true;
        if (size < options.MinFileSize) return true;
        if (options.MaxFileSize is long max && max >= 0 && size > max) return true;
        if (IsExcludedExtension(path, options)) return true;
        if (HasExcludedDirectoryName(path, options)) return true;
        return false;
    }

    public static bool IsExcludedByPath(string path, ScanOptions options)
        => IsExcludedExtension(path, options) || HasExcludedDirectoryName(path, options);

    public static bool ShouldSkipDirectoryName(string name, ScanOptions options)
    {
        if (options.ExcludedDirectoryNames.Count == 0) return false;
        foreach (var raw in options.ExcludedDirectoryNames)
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            if (string.Equals(name, item, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsExcludedExtension(string path, ScanOptions options)
    {
        if (options.ExcludedExtensions.Count == 0) return false;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        foreach (var raw in options.ExcludedExtensions)
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            if (item[0] != '.') item = "." + item;
            if (string.Equals(ext, item, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasExcludedDirectoryName(string path, ScanOptions options)
    {
        if (options.ExcludedDirectoryNames.Count == 0) return false;

        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            // Не сравниваем "C:" как каталог.
            if (part.EndsWith(':')) continue;
            if (ShouldSkipDirectoryName(part, options)) return true;
        }
        return false;
    }
}
