namespace Deduplicator.App.ViewModels.Items;

/// <summary>
/// Сегмент «хлебных крошек» проводника — одна папка пути. Для подключённого тома хранит
/// абсолютный путь на диске, для слепка отключённого тома — узел построенного из базы дерева
/// (null и там и там — корень слепка: его дети лежат в корневой коллекции).
/// </summary>
public sealed class ExplorerCrumb
{
    public ExplorerCrumb(string title, string? livePath, ExplorerNode? snapNode)
    {
        Title = title;
        LivePath = livePath;
        SnapNode = snapNode;
    }

    public string Title { get; }

    /// <summary>Абсолютный путь папки на подключённом диске (null для слепка).</summary>
    public string? LivePath { get; }

    /// <summary>Узел слепка отключённого тома (null для живого тома и для корня слепка).</summary>
    public ExplorerNode? SnapNode { get; }
}
