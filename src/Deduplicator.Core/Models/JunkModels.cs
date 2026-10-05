namespace Deduplicator.Core.Models;

/// <summary>Вид «мусора» для очистки диска.</summary>
public enum JunkKind
{
    EmptyFolder = 0,
    ZeroByteFile = 1,
    TempFile = 2,
    BrokenShortcut = 3,
    LargeFile = 4
}

/// <summary>Параметры поиска мусора.</summary>
public sealed class JunkScanOptions
{
    public List<string> Roots { get; set; } = new();
    public bool EmptyFolders { get; set; } = true;
    public bool ZeroByteFiles { get; set; } = true;
    public bool TempFiles { get; set; } = true;
    public bool BrokenShortcuts { get; set; } = true;
    public bool LargeFiles { get; set; } = false;

    /// <summary>Порог «большого файла» (байты).</summary>
    public long LargeThresholdBytes { get; set; } = 1L * 1024 * 1024 * 1024; // 1 ГБ

    public bool SkipHiddenSystem { get; set; } = true;
}

/// <summary>Найденный элемент мусора.</summary>
public sealed class JunkItem
{
    public JunkKind Kind { get; set; }
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset Modified { get; set; }
    public bool IsFolder { get; set; }
}
