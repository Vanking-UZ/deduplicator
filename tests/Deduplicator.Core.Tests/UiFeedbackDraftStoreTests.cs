using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

public sealed class UiFeedbackDraftStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dedup_feedback_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_then_load_round_trips_a_bounded_draft_atomically()
    {
        var paths = new AppPaths(_dir);
        var store = new UiFeedbackDraftStore(paths);
        var draft = new UiFeedbackDraft
        {
            GeneralComment = "Нужен более заметный итог поиска.",
            Entries =
            {
                new UiFeedbackEntry
                {
                    Page = "Results",
                    ElementId = "delete-duplicates",
                    ElementLabel = "results.delete-copies",
                    Comment = "Сделать кнопку спокойнее и добавить пояснение."
                },
                new UiFeedbackEntry
                {
                    Page = "Home",
                    ElementId = "start-scan",
                    ElementLabel = "home.start-scan",
                    Comment = "Показать, что будет проверяться."
                }
            }
        };

        store.Save(draft);
        var loaded = store.Load();

        Assert.Equal(draft.GeneralComment, loaded.GeneralComment);
        Assert.Collection(loaded.Entries,
            entry =>
            {
                Assert.Equal("Results", entry.Page);
                Assert.Equal("delete-duplicates", entry.ElementId);
                Assert.Equal("results.delete-copies", entry.ElementLabel);
            },
            entry => Assert.Equal("home.start-scan", entry.ElementLabel));
        Assert.Empty(Directory.GetFiles(_dir, "*" + SafeIo.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void Load_returns_empty_draft_for_corrupted_or_oversized_file()
    {
        var paths = new AppPaths(_dir);
        var store = new UiFeedbackDraftStore(paths);

        File.WriteAllText(paths.FeedbackDraftPath, "{ definitely not json");
        Assert.Empty(store.Load().Entries);

        File.WriteAllText(paths.FeedbackDraftPath, new string('x', UiFeedbackDraftStore.MaxDraftBytes + 1));
        var loaded = store.Load();

        Assert.Empty(loaded.Entries);
        Assert.Equal(string.Empty, loaded.GeneralComment);
    }

    [Fact]
    public void Save_rejects_excessive_comments_entries_and_serialized_size()
    {
        var store = new UiFeedbackDraftStore(new AppPaths(_dir));

        Assert.Throws<ArgumentException>(() => store.Save(new UiFeedbackDraft
        {
            GeneralComment = new string('x', UiFeedbackDraftStore.MaxCommentCharacters + 1)
        }));

        var tooManyEntries = new UiFeedbackDraft();
        for (var i = 0; i <= UiFeedbackDraftStore.MaxEntries; i++)
            tooManyEntries.Entries.Add(new UiFeedbackEntry());
        Assert.Throws<ArgumentException>(() => store.Save(tooManyEntries));

        Assert.Throws<ArgumentException>(() => store.Save(new UiFeedbackDraft
        {
            Entries = { new UiFeedbackEntry { ElementLabel = new string('я', UiFeedbackDraftStore.MaxDraftBytes) } }
        }));
    }

    [Fact]
    public void Concurrent_saves_leave_a_complete_valid_draft()
    {
        var store = new UiFeedbackDraftStore(new AppPaths(_dir));

        Parallel.For(0, 32, i => store.Save(new UiFeedbackDraft
        {
            GeneralComment = $"Комментарий {i}",
            Entries = { new UiFeedbackEntry { ElementId = $"element-{i}", Comment = "ok" } }
        }));

        var loaded = store.Load();
        Assert.Single(loaded.Entries);
        Assert.StartsWith("Комментарий ", loaded.GeneralComment);
        Assert.StartsWith("element-", loaded.Entries[0].ElementId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }
}
