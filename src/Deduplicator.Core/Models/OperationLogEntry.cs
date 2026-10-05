namespace Deduplicator.Core.Models;

/// <summary>Запись журнала операций — основа обратимости (восстановление из карантина/корзины).</summary>
public sealed class OperationLogEntry
{
    public long Id { get; set; }
    public OperationType OpType { get; set; }
    public long? FileId { get; set; }
    public long? GroupId { get; set; }

    /// <summary>Исходный путь файла до операции.</summary>
    public string? SrcPath { get; set; }

    /// <summary>Куда перемещён (папка карантина или метка корзины).</summary>
    public string? DstPath { get; set; }

    public DateTimeOffset PerformedAt { get; set; }

    /// <summary>Можно ли отменить операцию (восстановить файл).</summary>
    public bool Reversible { get; set; }

    /// <summary>Уже восстановлено.</summary>
    public bool Restored { get; set; }

    // Для отображения
    public string? FileName { get; set; }
    public long? SizeBytes { get; set; }
}
