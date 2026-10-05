namespace Deduplicator.Core.Models;

/// <summary>
/// Носитель (том): отдельное физическое или логическое хранилище, идентифицируемое
/// серийным номером тома. Хранится в каталоге даже после отключения.
/// </summary>
public sealed class VolumeRecord
{
    public long Id { get; set; }

    /// <summary>Метка тома (например, «Архив фото»).</summary>
    public string? Label { get; set; }

    /// <summary>Серийный номер тома — устойчивый идентификатор носителя.</summary>
    public string? Serial { get; set; }

    /// <summary>Тип файловой системы (NTFS, exFAT, ...).</summary>
    public string? FsType { get; set; }

    /// <summary>Общий объём носителя в байтах.</summary>
    public long TotalBytes { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? LastScanned { get; set; }

    // --- Состояние времени выполнения (в БД не хранится) ---

    /// <summary>Подключён ли носитель прямо сейчас.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Текущая точка монтирования (буква диска), если подключён.</summary>
    public string? MountPath { get; set; }

    /// <summary>Свободно байт сейчас (если подключён).</summary>
    public long? FreeBytes { get; set; }

    /// <summary>Количество проиндексированных файлов (для сводки каталога).</summary>
    public long IndexedFileCount { get; set; }

    /// <summary>Съёмный носитель (USB/флешка).</summary>
    public bool IsRemovable { get; set; }

    /// <summary>Сетевой/подключённый диск (UNC/маппинг). Скорость зависит от сети (P1.4).</summary>
    public bool IsNetwork { get; set; }

    /// <summary>Тип носителя по WMI: 3 = HDD, 4 = SSD, 0 = неизвестно.</summary>
    public int MediaType { get; set; }

    /// <summary>
    /// Есть ли у носителя штраф позиционирования (IOCTL_STORAGE_QUERY_PROPERTY / seek-penalty):
    /// true — механика (HDD, в т.ч. внешний в USB-боксе, который WMI видит как «Fixed unknown»),
    /// false — флэш/SSD (в т.ч. СЪЁМНЫЕ USB-SSD и флешки), null — неизвестно (сеть/нет доступа).
    /// Первичный сигнал для плана чтения — точнее WMI-типа (этап 1.2 дорожной карты).
    /// </summary>
    public bool? HasSeekPenalty { get; set; }

    /// <summary>
    /// Номер физического диска Windows (MSFT_Partition.DiskNumber). Runtime-only: нужен планировщику
    /// одного скана, в каталог БД не сохраняется и между перезагрузками не считается идентичностью тома.
    /// </summary>
    public uint? PhysicalDiskNumber { get; set; }

    /// <summary>
    /// Стабильный в пределах текущего запуска ключ физического устройства. Разделы одного диска
    /// получают один ключ; при недоступной топологии используется консервативный volume/network key.
    /// </summary>
    public string? PhysicalDeviceKey { get; set; }

    public bool IsHdd => MediaType == 3;
    public bool IsSsd => MediaType == 4;

    /// <summary>
    /// Сериализовать ли чтение содержимого (NFR-7, P1.4). Первичен seek-penalty: съёмный USB3-SSD
    /// получает параллельные чтения (2–4x на хэшировании), а HDD в USB-боксе — последовательные
    /// (без seek-шторма). Фолбэк при неизвестном — прежняя консервативная эвристика.
    /// </summary>
    public bool PreferSequentialReads => HasSeekPenalty ?? (IsHdd || IsRemovable || IsNetwork);

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Label) ? (MountPath ?? Serial ?? $"Диск №{Id}") : Label!;
            return IsNetwork ? name + " (сетевой)" : name;
        }
    }
}
