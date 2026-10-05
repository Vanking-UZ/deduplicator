using Deduplicator.Core.Data;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Services;

public interface IStatisticsService
{
    DuplicateStatistics Compute();
}

/// <summary>Сводная статистика для экрана «Статистика» (FR-24).</summary>
public sealed class StatisticsService : IStatisticsService
{
    private readonly GroupRepository _groups;
    private readonly FileRepository _files;

    public StatisticsService(GroupRepository groups, FileRepository files)
    {
        _groups = groups;
        _files = files;
    }

    public DuplicateStatistics Compute()
    {
        var stats = _groups.GetStatistics();
        var (files, bytes) = _files.TotalIndexed();
        stats.TotalIndexedFiles = files;
        stats.TotalIndexedBytes = bytes;

        var (byCount, byBytes) = _files.CategoryBreakdown();
        stats.FilesByCategory = byCount;
        stats.BytesByCategory = byBytes;
        return stats;
    }
}
