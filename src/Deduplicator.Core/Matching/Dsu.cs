namespace Deduplicator.Core.Matching;

/// <summary>Система непересекающихся множеств (union-find) для слияния пар в группы.</summary>
public sealed class Dsu
{
    private readonly Dictionary<long, long> _parent = new();
    private readonly Dictionary<long, int> _rank = new();

    public void MakeSet(long x)
    {
        if (!_parent.ContainsKey(x)) { _parent[x] = x; _rank[x] = 0; }
    }

    public long Find(long x)
    {
        MakeSet(x);
        long root = x;
        while (_parent[root] != root) root = _parent[root];
        // Сжатие пути
        while (_parent[x] != root) { var next = _parent[x]; _parent[x] = root; x = next; }
        return root;
    }

    public void Union(long a, long b)
    {
        long ra = Find(a), rb = Find(b);
        if (ra == rb) return;
        if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
        _parent[rb] = ra;
        if (_rank[ra] == _rank[rb]) _rank[ra]++;
    }

    /// <summary>Сгруппировать ранее добавленные элементы по их множествам.</summary>
    public Dictionary<long, List<long>> Groups()
    {
        var result = new Dictionary<long, List<long>>();
        foreach (var x in _parent.Keys)
        {
            var root = Find(x);
            if (!result.TryGetValue(root, out var list))
                result[root] = list = new List<long>();
            list.Add(x);
        }
        return result;
    }
}
