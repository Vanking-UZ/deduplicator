using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>
/// Публичная обёртка над определением физической идентичности файла (NTFS volume serial + file index).
/// Нужна продуктовому grouper-у, чтобы схлопывать hardlink-алиасы и не завышать освобождаемое место.
/// Windows-only; при любой ошибке возвращает false.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileIdentityReader
{
    public static bool TryGet(string path, out FileIdentity identity) =>
        Native.TryGetFileIdentity(path, out identity);
}
