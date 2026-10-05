namespace Deduplicator.Core.Models;

/// <summary>Группа дубликатов: набор файлов, признанных копиями/похожими.</summary>
public sealed class DuplicateGroup
{
    public long Id { get; set; }
    public MatchType MatchType { get; set; }

    /// <summary>1.0 — для точных; &lt;1.0 — средняя схожесть для похожих.</summary>
    public double Confidence { get; set; }

    /// <summary>Эталон группы (его удалять нельзя).</summary>
    public long? RepresentativeId { get; set; }

    /// <summary>Суммарный размер лишних копий, который можно освободить.</summary>
    public long ReclaimableBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<DuplicateMember> Members { get; set; } = new();
}

/// <summary>Член группы дубликатов с привязанным слепком файла.</summary>
public sealed class DuplicateMember
{
    public long GroupId { get; set; }
    public long FileId { get; set; }

    /// <summary>Схожесть с эталоном (0..1); для точных копий — 1.0.</summary>
    public double Similarity { get; set; }

    public bool IsRepresentative { get; set; }

    /// <summary>Привязанный слепок (заполняется при выборке).</summary>
    public FileRecord? File { get; set; }
}
