using System.Security.Cryptography;
using System.Text;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Engine;

/// <summary>Отпечаток только тех настроек, которые меняют состав метаданных индекса.</summary>
public static class ScanOptionsFingerprint
{
    public static string ForEnumeration(ScanOptions options)
    {
        var extensions = string.Join(",", options.ExcludedExtensions
            .Select(x => x.Trim().ToLowerInvariant())
            .OrderBy(x => x, StringComparer.Ordinal));
        var categories = string.Join(",", options.IncludedCategories.Select(c => (int)c).OrderBy(x => x));
        // Depth/пороги похожести/правило эталона относятся к последующим стадиям и не требуют
        // повторного обхода диска. Удаление Depth отсюда делает переключение «+ похожие фото»
        // мгновенным на неизменившемся томе, сохраняя USN-корректность.
        var payload = $"{options.MinFileSize}|{options.MaxFileSize}|{options.SkipHiddenSystem}|" +
                      $"{extensions}|{categories}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
