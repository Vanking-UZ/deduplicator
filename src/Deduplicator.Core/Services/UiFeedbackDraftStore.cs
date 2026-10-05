using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>Локальное хранилище черновика замечаний к интерфейсу.</summary>
public interface IUiFeedbackDraftStore
{
    UiFeedbackDraft Load();
    void Save(UiFeedbackDraft draft);
}

/// <summary>
/// Хранит один небольшой JSON-черновик. Запись публикуется атомарно, а общий
/// на путь замок не даёт нескольким экземплярам сервиса перемешать сохранения.
/// Повреждённый или чрезмерно большой файл никогда не попадает в интерфейс.
/// </summary>
public sealed class UiFeedbackDraftStore : IUiFeedbackDraftStore
{
    public const int MaxDraftBytes = 1024 * 1024;
    public const int MaxEntries = 300;
    public const int MaxCommentCharacters = 4_000;

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate;

    public UiFeedbackDraftStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.FeedbackDraftPath;
        _gate = PathLocks.GetOrAdd(Path.GetFullPath(_path), static _ => new object());
    }

    public UiFeedbackDraft Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return EmptyDraft();

                var draft = JsonSerializer.Deserialize<UiFeedbackDraft>(
                    SafeIo.ReadAllTextLimited(_path, MaxDraftBytes), JsonOptions);

                return IsValid(draft) ? Normalize(draft!) : EmptyDraft();
            }
            catch
            {
                // Черновик не влияет на основную работу программы. Не даём
                // повреждённому/подменённому файлу сорвать запуск интерфейса.
                return EmptyDraft();
            }
        }
    }

    public void Save(UiFeedbackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IsValid(draft))
            throw new ArgumentException(
                $"Черновик может содержать не более {MaxEntries} пометок и комментарии до {MaxCommentCharacters:N0} символов.",
                nameof(draft));

        var normalized = Normalize(draft);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxDraftBytes)
            throw new ArgumentException($"Черновик не может превышать {MaxDraftBytes / 1024:N0} КБ.", nameof(draft));

        lock (_gate)
        {
            SafeIo.WriteAllTextAtomic(_path, json);
        }
    }

    private static bool IsValid(UiFeedbackDraft? draft)
    {
        if (draft is null || draft.Entries is null || draft.Entries.Count > MaxEntries ||
            (draft.GeneralComment?.Length ?? 0) > MaxCommentCharacters)
            return false;

        return draft.Entries.All(entry => entry is not null &&
            (entry.Comment?.Length ?? 0) <= MaxCommentCharacters);
    }

    private static UiFeedbackDraft Normalize(UiFeedbackDraft draft) => new()
    {
        GeneralComment = draft.GeneralComment ?? string.Empty,
        Entries = draft.Entries.Select(entry => new UiFeedbackEntry
        {
            Page = entry.Page ?? string.Empty,
            ElementId = entry.ElementId ?? string.Empty,
            ElementLabel = entry.ElementLabel ?? string.Empty,
            Comment = entry.Comment ?? string.Empty
        }).ToList()
    };

    private static UiFeedbackDraft EmptyDraft() => new();
}
