using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Deduplicator.App.ViewModels;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.Tests;

public sealed class FeedbackPickerViewModelTests
{
    [AvaloniaFact]
    public void Picker_collects_many_notes_applies_a_shared_comment_and_saves_a_safe_report()
    {
        var store = new InMemoryFeedbackStore();
        var viewModel = new FeedbackPickerViewModel(store);
        var first = new Button { Name = "HomeToolScan" };
        var second = new Button { Name = "SavedFilesToolButton" };

        viewModel.TogglePickerCommand.Execute(null);
        viewModel.ToggleSelection(first, new FeedbackTargetDescriptor(
            "HomeViewModel", "Button:HomeToolScan", "Button · Button:HomeToolScan"));
        viewModel.ToggleSelection(second, new FeedbackTargetDescriptor(
            "HomeViewModel", "Button:SavedFilesToolButton", "Button · Button:SavedFilesToolButton"));

        viewModel.SharedComment = "Сделать эти действия заметнее";
        viewModel.ApplySharedCommentCommand.Execute(null);
        viewModel.GeneralComment = "Интерфейс должен оставаться понятным с первого взгляда.";
        viewModel.SaveDraftCommand.Execute(null);

        Assert.True(viewModel.IsPanelOpen);
        Assert.True(viewModel.IsPickerActive);
        Assert.Equal(2, viewModel.Items.Count);
        Assert.All(viewModel.Items, item => Assert.Equal("Сделать эти действия заметнее", item.Comment));
        Assert.Equal(2, store.Draft.Entries.Count);

        string report = viewModel.BuildMarkdownReport();
        Assert.Contains("HomeToolScan", report);
        Assert.Contains("Сделать эти действия заметнее", report);
        Assert.DoesNotContain("C:\\", report);
    }

    [AvaloniaFact]
    public void Restored_note_reconnects_to_its_control_and_can_be_removed_without_duplicate()
    {
        var store = new InMemoryFeedbackStore
        {
            Draft = new UiFeedbackDraft
            {
                Entries =
                {
                    new UiFeedbackEntry
                    {
                        Page = "ScanViewModel",
                        ElementId = "Button:OnboardingScanStart",
                        ElementLabel = "Button · Button:OnboardingScanStart",
                        Comment = "Уточнить действие"
                    }
                }
            }
        };
        var viewModel = new FeedbackPickerViewModel(store);
        var target = new Button { Name = "OnboardingScanStart" };
        var descriptor = new FeedbackTargetDescriptor(
            "ScanViewModel", "Button:OnboardingScanStart", "Button · Button:OnboardingScanStart");

        viewModel.ToggleSelection(target, descriptor);
        Assert.Single(viewModel.Items);
        Assert.Equal("Уточнить действие", viewModel.Items[0].Comment);

        viewModel.ToggleSelection(target, descriptor);
        Assert.Empty(viewModel.Items);
    }

    [AvaloniaFact]
    public void Picker_keeps_text_cards_and_small_ui_elements_as_separate_notes()
    {
        var viewModel = new FeedbackPickerViewModel(new InMemoryFeedbackStore());
        var text = new TextBlock { Text = "Подпись" };
        var card = new Border { Width = 120, Height = 48 };
        var badge = new Border { Width = 24, Height = 24 };

        viewModel.ToggleSelection(text, new FeedbackTargetDescriptor(
            "HomeViewModel", "TextBlock[0]", "Текст · TextBlock[0]"));
        viewModel.ToggleSelection(card, new FeedbackTargetDescriptor(
            "HomeViewModel", "Border[1]", "Карточка · Border[1]"));
        viewModel.ToggleSelection(badge, new FeedbackTargetDescriptor(
            "HomeViewModel", "Border[2]", "Плашка · Border[2]"));

        Assert.Equal(3, viewModel.Items.Count);
        Assert.Equal(new[] { "Текст", "Карточка", "Плашка" },
            viewModel.Items.Select(item => item.ElementLabel.Split(' ')[0]).ToArray());
    }

    [Fact]
    public void Picker_can_hide_its_panel_without_stopping_selection_mode()
    {
        var viewModel = new FeedbackPickerViewModel(new InMemoryFeedbackStore());
        viewModel.TogglePickerCommand.Execute(null);

        viewModel.HidePickerPanelCommand.Execute(null);

        Assert.True(viewModel.IsPickerActive);
        Assert.False(viewModel.IsPanelOpen);
    }

    private sealed class InMemoryFeedbackStore : IUiFeedbackDraftStore
    {
        private readonly object _gate = new();

        public UiFeedbackDraft Draft { get; set; } = new();

        public UiFeedbackDraft Load()
        {
            lock (_gate) return Copy(Draft);
        }

        public void Save(UiFeedbackDraft draft)
        {
            lock (_gate) Draft = Copy(draft);
        }

        private static UiFeedbackDraft Copy(UiFeedbackDraft draft) => new()
        {
            GeneralComment = draft.GeneralComment,
            Entries = draft.Entries.Select(entry => new UiFeedbackEntry
            {
                Page = entry.Page,
                ElementId = entry.ElementId,
                ElementLabel = entry.ElementLabel,
                Comment = entry.Comment
            }).ToList()
        };
    }
}
