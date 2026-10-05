using System.Runtime.InteropServices;

namespace Deduplicator.Core.Util;

/// <summary>
/// Поддержка путей длиннее 260 символов через префикс <c>\\?\</c> (NFR-1).
/// На не-Windows платформах возвращает путь без изменений.
/// </summary>
public static class LongPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Возвращает путь, безопасный для длинных значений на Windows.
    /// Применяется непосредственно перед обращением к ФС (File/Directory API).
    /// </summary>
    public static string Fix(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return path;
        if (string.IsNullOrEmpty(path) || path.StartsWith(Prefix, StringComparison.Ordinal))
            return path;

        // Префикс осмыслен только для абсолютных путей.
        if (!Path.IsPathRooted(path))
            return path;

        var full = path.Replace('/', '\\');

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC: \\server\share -> \\?\UNC\server\share (снимаем все ведущие слэши)
            return UncPrefix + full.TrimStart('\\');
        }
        return Prefix + full;
    }
}
