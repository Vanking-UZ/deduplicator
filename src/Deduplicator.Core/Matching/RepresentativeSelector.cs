using Deduplicator.Core.Models;

namespace Deduplicator.Core.Matching;

/// <summary>Выбор эталона группы по настраиваемому правилу (FR-12, FR-36).</summary>
public static class RepresentativeSelector
{
    /// <param name="isPreferredKeep">
    /// Предикат «файл в папке-эталоне» для правила <see cref="RepresentativeRule.Smart"/> (2.10).
    /// Core сам не знает настроек и точек монтирования — предикат строит фасад (ScanService) и
    /// прокидывает сюда. null — бонус папки-эталона просто не начисляется, остальные эвристики работают.
    /// </param>
    public static FileRecord Pick(IReadOnlyList<FileRecord> files, RepresentativeRule rule,
        Func<FileRecord, bool>? isPreferredKeep = null)
    {
        if (files.Count == 0) throw new ArgumentException("Пустая группа", nameof(files));
        return rule switch
        {
            RepresentativeRule.Oldest => files.OrderBy(f => f.Mtime).ThenBy(f => f.RelPath).First(),
            RepresentativeRule.Newest => files.OrderByDescending(f => f.Mtime).ThenBy(f => f.RelPath).First(),
            RepresentativeRule.ShortestPath => files.OrderBy(f => f.RelPath.Length).ThenBy(f => f.Mtime).First(),
            RepresentativeRule.LongestName => files.OrderByDescending(f => f.Name.Length).ThenBy(f => f.Mtime).First(),
            RepresentativeRule.Smart => files[KeepScorer.PickIndex(ToCandidates(files, isPreferredKeep))],
            _ => files[0]
        };
    }

    /// <summary>Слепки кандидатов для <see cref="KeepScorer"/> (2.10) из записей группы.</summary>
    public static IReadOnlyList<KeepCandidate> ToCandidates(IReadOnlyList<FileRecord> files,
        Func<FileRecord, bool>? isPreferredKeep = null)
    {
        var list = new KeepCandidate[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            list[i] = new KeepCandidate(f.RelPath, f.Name, f.Created, f.Mtime, f.Size,
                isPreferredKeep?.Invoke(f) ?? false);
        }
        return list;
    }
}
