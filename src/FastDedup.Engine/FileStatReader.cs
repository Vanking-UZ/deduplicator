using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>
/// Публичная обёртка над дешёвым stat одним системным вызовом (GetFileAttributesExW).
/// Нужна продуктовому дельта-реконсайлу USN: изменённый по журналу файл пере-статится с диска
/// и уходит в ТОТ ЖЕ путь индексирования, что при обычном перечислении. null — файла нет или
/// он недоступен (вызывающий решает: удалить строку индекса или счесть носитель отключённым).
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileStatReader
{
    public static EnumeratedFile? TryGet(string path) =>
        Native.TryGetFileInfo(path, out long size, out uint attrs, out long creationRaw, out long lastWriteRaw)
            ? new EnumeratedFile(path, size, creationRaw, lastWriteRaw, attrs)
            : null;
}
