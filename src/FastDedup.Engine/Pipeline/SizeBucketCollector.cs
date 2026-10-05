using System.Collections.Concurrent;

namespace FastDedup.Engine;

/// <summary>
/// Потокобезопасный сбор bucket-ов по размеру без промежуточной очереди всех FileEntry
/// и без List-аллокации для каждого уникального размера. Уникальные размеры держат только
/// один FileEntry; список создаётся лениво при появлении второго файла того же размера.
/// </summary>
internal sealed class SizeBucketCollector
{
    private sealed class Bucket
    {
        private FileEntry? _first;
        private List<FileEntry>? _many;
        private int _count;

        // Лочим на самом Bucket (тип приватный вложенный, наружу не утекает — lock(this) безопасен).
        // Экономит по одному объекту-замку на каждый уникальный размер (на миллионах размеров заметно).
        public void Add(FileEntry entry)
        {
            lock (this)
            {
                if (_count == 0)
                {
                    _first = entry;
                }
                else
                {
                    _many ??= new List<FileEntry>(capacity: 2) { _first! };
                    _many.Add(entry);
                }
                _count++;
            }
        }

        public List<FileEntry>? ToDuplicateGroupOrNull()
        {
            lock (this)
            {
                return _count >= 2 && _many is not null ? new List<FileEntry>(_many) : null;
            }
        }
    }

    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();

    public void Add(FileEntry entry)
        => _buckets.GetOrAdd(entry.Size, _ => new Bucket()).Add(entry);

    public List<List<FileEntry>> GetDuplicateGroups()
    {
        var result = new List<List<FileEntry>>();
        foreach (var bucket in _buckets.Values)
        {
            var group = bucket.ToDuplicateGroupOrNull();
            if (group is not null) result.Add(group);
        }
        return result;
    }
}
