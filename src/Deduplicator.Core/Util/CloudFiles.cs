namespace Deduplicator.Core.Util;

/// <summary>
/// Определение облачных файлов-плейсхолдеров (OneDrive «Файлы по запросу», Storage Sense) и
/// reparse-точек. Контентные этапы (хэш, побайтовая сверка, pHash, миниатюры, ревалидация перед
/// удалением) обязаны пропускать такие файлы: чтение дегидратированного плейсхолдера заставляет
/// провайдера скачать содержимое из облака — офлайн-приложение не должно инициировать сетевой
/// трафик и заполнять диск чужими загрузками. Перечисление плейсхолдеры и так не индексирует;
/// эта проверка закрывает путь через УСТАРЕВШИЕ записи БД (файл был локальным на прошлом скане,
/// потом «освободили место»).
/// </summary>
public static class CloudFiles
{
    // Этих значений нет в enum FileAttributes (.NET 8) — берём из Windows SDK.
    private const int RecallOnOpen = 0x00040000;        // FILE_ATTRIBUTE_RECALL_ON_OPEN
    private const int RecallOnDataAccess = 0x00400000;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    private const int CloudMask =
        (int)(FileAttributes.Offline | FileAttributes.ReparsePoint) | RecallOnOpen | RecallOnDataAccess;

    /// <summary>Плейсхолдер/reparse: содержимое читать нельзя (спровоцирует гидратацию из облака).</summary>
    public static bool IsPlaceholder(FileAttributes attrs) => ((int)attrs & CloudMask) != 0;

    /// <summary>
    /// Проверка по пути. При недоступности атрибутов возвращает false (консервативно НЕ блокируем:
    /// реальную ошибку чтения поймает и корректно обработает сам контентный этап).
    /// </summary>
    public static bool IsPlaceholderPath(string path)
    {
        try { return IsPlaceholder(File.GetAttributes(LongPath.Fix(path))); }
        catch { return false; }
    }
}
