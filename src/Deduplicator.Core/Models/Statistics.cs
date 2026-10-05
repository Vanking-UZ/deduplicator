namespace Deduplicator.Core.Models;

/// <summary>Сводная статистика по найденным дубликатам.</summary>
public sealed class DuplicateStatistics
{
    public int ExactGroups { get; set; }
    public int SimilarImageGroups { get; set; }
    public int SimilarAudioGroups { get; set; }
    public int ExactVideoGroups { get; set; }

    public long ExactReclaimableBytes { get; set; }
    public long SimilarImageReclaimableBytes { get; set; }
    public long SimilarAudioReclaimableBytes { get; set; }
    public long ExactVideoReclaimableBytes { get; set; }

    public long TotalDuplicateFiles { get; set; }
    public long TotalReclaimableBytes =>
        ExactReclaimableBytes + SimilarImageReclaimableBytes +
        SimilarAudioReclaimableBytes + ExactVideoReclaimableBytes;

    public int TotalGroups =>
        ExactGroups + SimilarImageGroups + SimilarAudioGroups + ExactVideoGroups;

    /// <summary>Разбивка проиндексированных файлов по категориям (для диаграммы).</summary>
    public Dictionary<FileCategory, long> FilesByCategory { get; set; } = new();

    /// <summary>Разбивка занятого объёма по категориям.</summary>
    public Dictionary<FileCategory, long> BytesByCategory { get; set; } = new();

    public long TotalIndexedFiles { get; set; }
    public long TotalIndexedBytes { get; set; }
}
