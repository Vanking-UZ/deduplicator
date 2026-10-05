namespace FastDedup.Engine;

/// <summary>
/// MFT/USN-перечисление невозможно для этого тома (нет прав, неподдерживаемые записи, сбой API).
/// Бросается <see cref="UsnEnumerator"/>; ловится <see cref="FastFileLister"/>, который переключается
/// на совместимый быстрый <see cref="Win32FindMetadataEnumerator"/> (а НЕ на старый Directory+stat).
/// </summary>
public sealed class MftEnumerationUnavailableException : Exception
{
    public BackendFallbackReason Reason { get; }

    public MftEnumerationUnavailableException(BackendFallbackReason reason, string message)
        : base(message) => Reason = reason;
}
