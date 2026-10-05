using System.Collections.Concurrent;

namespace FastDedup.Engine;

/// <summary>
/// Стратегия перечисления файлов. Оркестратор выбирает реализацию по носителю/правам:
/// быстрый USN/MFT-обход на NTFS под админом, иначе обычный рекурсивный обход.
/// </summary>
public interface IFileEnumerator
{
    IEnumerable<string> EnumerateFiles(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct);
}
