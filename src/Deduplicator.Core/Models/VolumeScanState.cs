namespace Deduplicator.Core.Models;

/// <summary>
/// Сохранённое состояние USN-журнала тома на момент последнего полного индексирования.
/// Позволяет следующему скану решить: переиспользовать индекс (инкрементально) или пересобрать.
/// </summary>
public sealed class VolumeScanState
{
    public long VolumeId { get; set; }

    /// <summary>Идентификатор журнала USN (меняется при пересоздании журнала).</summary>
    public long JournalId { get; set; }

    /// <summary>USN, на котором остановились в прошлый раз (с него читать изменения).</summary>
    public long NextUsn { get; set; }

    public DateTimeOffset? IndexedAt { get; set; }

    /// <summary>Версия движка — при изменении формата индекса заставляет пересобрать.</summary>
    public int EngineVersion { get; set; }

    /// <summary>Хэш параметров скана (фильтры): при изменении параметров нужен полный пересбор.</summary>
    public string? OptionsHash { get; set; }
}
