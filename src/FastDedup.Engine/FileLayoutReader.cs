using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>
/// Публичная обёртка над физическим размещением файла на диске: первый LCN через
/// FSCTL_GET_RETRIEVAL_POINTERS (права администратора НЕ нужны). Нужна продуктовому движку,
/// чтобы на HDD сортировать очередь чтений по позиции головки, а не по порядку строк в БД.
/// Windows/NTFS; при любой ошибке (не-NTFS, резидентный файл, нет доступа) возвращает null.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileLayoutReader
{
    public static long? TryGetFirstLcn(string path) => Native.TryGetFirstLcn(path);
}
