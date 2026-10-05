using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>
/// Local, opt-in UI feedback picker.  It stores only stable UI identifiers and
/// comments entered by the user; it never reads element text, file paths or data
/// that happens to be displayed by the application.
/// </summary>
public sealed partial class FeedbackPickerViewModel : ObservableObject
{
    private const int MaxSelections = UiFeedbackDraftStore.MaxEntries;
    // Keeping the interactive editor concise also guarantees that a fully filled
    // draft comfortably stays below the 1 MiB storage guardrail.
    public const int MaxCommentLength = 300;
    private const int MaxPageLength = 64;
    private const int MaxElementIdLength = 120;
    private const int MaxElementLabelLength = 160;

    private readonly IUiFeedbackDraftStore _store;
    private long _draftRevision;

    public FeedbackPickerViewModel(IUiFeedbackDraftStore store)
    {
        _store = store;
        Items.CollectionChanged += OnItemsChanged;
        LoadDraft();
    }

    public ObservableCollection<FeedbackSelectionItem> Items { get; } = new();

    /// <summary>Provided by the window so this view model remains UI-host agnostic.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [ObservableProperty] private bool _isPickerActive;
    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private string _generalComment = string.Empty;
    [ObservableProperty] private string _sharedComment = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    public bool HasSelections => Items.Count > 0;
    public bool HasDraft => HasSelections || !string.IsNullOrWhiteSpace(GeneralComment);
    public bool CanApplySharedComment => HasSelections && !string.IsNullOrWhiteSpace(SharedComment);
    public string SelectionSummary => HasSelections
        ? $"Отмечено элементов: {Items.Count}"
        : "Нажимайте на элементы, которые хочется изменить";
    public string PickerButtonText => IsPickerActive ? "Завершить отметки" : "Отметить интерфейс";

    [RelayCommand]
    private void TogglePicker()
    {
        IsPanelOpen = true;
        IsPickerActive = !IsPickerActive;
        StatusText = IsPickerActive
            ? "Режим отметок включён: выберите несколько элементов на экране."
            : "Отметки готовы. Можно оставить комментарии ниже.";
    }

    [RelayCommand]
    private void StopPicker()
    {
        IsPickerActive = false;
        IsPanelOpen = true;
        StatusText = "Отметки готовы. Можно оставить комментарии ниже.";
    }

    [RelayCommand]
    private void ClosePanel()
    {
        IsPickerActive = false;
        IsPanelOpen = false;
    }

    [RelayCommand]
    private void HidePickerPanel()
    {
        if (!IsPickerActive) return;
        IsPanelOpen = false;
        StatusText = "Панель скрыта: выбор продолжается. Завершите его кнопкой в боковом меню.";
    }

    [RelayCommand(CanExecute = nameof(CanApplySharedComment))]
    private void ApplySharedComment()
    {
        string comment = NormalizeComment(SharedComment);
        if (string.IsNullOrWhiteSpace(comment)) return;

        foreach (var item in Items)
            item.Comment = comment;

        SharedComment = string.Empty;
        StatusText = "Один комментарий добавлен ко всем отмеченным элементам.";
        SaveDraft();
    }

    [RelayCommand]
    private void SaveDraft()
    {
        try
        {
            // A manual save supersedes any delayed snapshot queued while typing.
            Interlocked.Increment(ref _draftRevision);
            _store.Save(BuildDraft());
            StatusText = "Черновик сохранён локально на этом компьютере.";
        }
        catch (Exception ex)
        {
            Core.Util.AppLog.Error("UiFeedback.SaveDraft", ex);
            StatusText = "Не удалось сохранить черновик. Он остаётся открытым в этом окне.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private async Task CopyReportAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            StatusText = "Не удалось открыть буфер обмена.";
            return;
        }

        try
        {
            await CopyToClipboardAsync(BuildMarkdownReport());
            StatusText = "Отчёт скопирован. Его можно вставить в сообщение или задачу.";
        }
        catch (Exception ex)
        {
            Core.Util.AppLog.Error("UiFeedback.CopyReport", ex);
            StatusText = "Не удалось скопировать отчёт.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private void ClearDraft()
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        GeneralComment = string.Empty;
        SharedComment = string.Empty;
        SaveDraft();
        StatusText = "Черновик очищен.";
    }

    [RelayCommand]
    private void RemoveSelection(FeedbackSelectionItem? item)
    {
        if (item is not null) Remove(item);
    }

    /// <summary>
    /// Toggle the selected state of a live control.  This method intentionally
    /// accepts a pre-sanitised descriptor, rather than inspecting the control's
    /// text or data context, so feedback cannot capture names, paths or file data.
    /// </summary>
    public void ToggleSelection(Control target, FeedbackTargetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(descriptor);

        var normalized = new FeedbackTargetDescriptor(
            NormalizeToken(descriptor.Page, MaxPageLength),
            NormalizeToken(descriptor.ElementId, MaxElementIdLength),
            NormalizeToken(descriptor.ElementLabel, MaxElementLabelLength));

        var itemForTarget = Items.FirstOrDefault(item =>
            item.Target is { } weak && weak.TryGetTarget(out var existing) && ReferenceEquals(existing, target));
        if (itemForTarget is not null)
        {
            Remove(itemForTarget);
            return;
        }

        // A loaded draft has no live visual reference.  Reconnecting the same
        // stable element should preserve its previous comment instead of creating
        // an indistinguishable duplicate.
        var restoredItem = Items.FirstOrDefault(item => item.Target is null &&
            string.Equals(item.Page, normalized.Page, StringComparison.Ordinal) &&
            string.Equals(item.ElementId, normalized.ElementId, StringComparison.Ordinal));
        if (restoredItem is not null)
        {
            restoredItem.Target = new WeakReference<Control>(target);
            StatusText = "Элемент снова связан с сохранённой отметкой.";
            return;
        }

        if (Items.Count >= MaxSelections)
        {
            StatusText = $"Можно отметить не больше {MaxSelections} элементов за раз.";
            return;
        }

        var item = new FeedbackSelectionItem(normalized.Page, normalized.ElementId, normalized.ElementLabel)
        {
            Number = Items.Count + 1,
            Target = new WeakReference<Control>(target)
        };
        Items.Add(item);
        StatusText = $"Элемент {item.Number} отмечен. Продолжайте выбирать или нажмите «Готово».";
    }

    public void SetOutline(FeedbackSelectionItem item, double x, double y, double width, double height, bool visible)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.OutlineX = x;
        item.OutlineY = y;
        item.OutlineWidth = width;
        item.OutlineHeight = height;
        item.IsOutlineVisible = visible;
    }

    public string BuildMarkdownReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Замечания к интерфейсу");
        builder.AppendLine();
        builder.AppendLine($"Отмечено элементов: {Items.Count}");
        builder.AppendLine();
        builder.AppendLine("## Общий комментарий");
        builder.AppendLine(string.IsNullOrWhiteSpace(GeneralComment)
            ? "—"
            : EscapeMarkdown(GeneralComment));

        foreach (var item in Items)
        {
            builder.AppendLine();
            builder.AppendLine($"## {item.Number}. {EscapeMarkdown(item.ElementLabel)}");
            builder.AppendLine($"- Экран: `{item.Page}`");
            builder.AppendLine($"- Элемент: `{item.ElementId}`");
            builder.AppendLine($"- Комментарий: {(string.IsNullOrWhiteSpace(item.Comment) ? "—" : EscapeMarkdown(item.Comment))}");
        }

        return builder.ToString();
    }

    private void LoadDraft()
    {
        try
        {
            var draft = _store.Load();
            GeneralComment = NormalizeComment(draft.GeneralComment);
            foreach (var entry in draft.Entries.Take(MaxSelections))
            {
                var item = new FeedbackSelectionItem(
                    NormalizeToken(entry.Page, MaxPageLength),
                    NormalizeToken(entry.ElementId, MaxElementIdLength),
                    NormalizeToken(entry.ElementLabel, MaxElementLabelLength),
                    NormalizeComment(entry.Comment))
                {
                    Number = Items.Count + 1
                };
                Items.Add(item);
            }

            if (Items.Count > 0 || !string.IsNullOrWhiteSpace(GeneralComment))
                StatusText = "Восстановлен локальный черновик отметок.";
        }
        catch (Exception ex)
        {
            Core.Util.AppLog.Error("UiFeedback.LoadDraft", ex);
        }
    }

    private UiFeedbackDraft BuildDraft() => new()
    {
        GeneralComment = NormalizeComment(GeneralComment),
        Entries = Items.Select(item => new UiFeedbackEntry
        {
            Page = NormalizeToken(item.Page, MaxPageLength),
            ElementId = NormalizeToken(item.ElementId, MaxElementIdLength),
            ElementLabel = NormalizeToken(item.ElementLabel, MaxElementLabelLength),
            Comment = NormalizeComment(item.Comment)
        }).ToList()
    };

    private void Remove(FeedbackSelectionItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        Renumber();
        StatusText = "Отметка снята.";
        SaveDraft();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (FeedbackSelectionItem item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems is not null)
            foreach (FeedbackSelectionItem item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;

        RefreshDraftState();
        ScheduleAutoSave();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeedbackSelectionItem.Comment))
        {
            RefreshDraftState();
            ScheduleAutoSave();
        }
    }

    partial void OnGeneralCommentChanged(string value)
    {
        RefreshDraftState();
        ScheduleAutoSave();
    }

    partial void OnSharedCommentChanged(string value)
    {
        OnPropertyChanged(nameof(CanApplySharedComment));
        ApplySharedCommentCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPickerActiveChanged(bool value) => OnPropertyChanged(nameof(PickerButtonText));

    partial void OnIsPanelOpenChanged(bool value) => OnPropertyChanged(nameof(PickerButtonText));

    private void RefreshDraftState()
    {
        OnPropertyChanged(nameof(HasSelections));
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(CanApplySharedComment));
        OnPropertyChanged(nameof(SelectionSummary));
        ApplySharedCommentCommand.NotifyCanExecuteChanged();
        CopyReportCommand.NotifyCanExecuteChanged();
        ClearDraftCommand.NotifyCanExecuteChanged();
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].Number = i + 1;
    }

    private void ScheduleAutoSave()
    {
        var snapshot = BuildDraft();
        long revision = Interlocked.Increment(ref _draftRevision);
        _ = SaveSnapshotLaterAsync(snapshot, revision);
    }

    private async Task SaveSnapshotLaterAsync(UiFeedbackDraft snapshot, long revision)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(450)).ConfigureAwait(false);
            if (Interlocked.Read(ref _draftRevision) != revision) return;
            await Task.Run(() => _store.Save(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Util.AppLog.Error("UiFeedback.AutoSave", ex);
        }
    }

    private static string NormalizeComment(string? value) => TrimToLength(value?.Trim() ?? string.Empty, MaxCommentLength);

    private static string NormalizeToken(string? value, int limit)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return TrimToLength(normalized, limit);
    }

    private static string TrimToLength(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    private static string EscapeMarkdown(string value)
    {
        var escaped = value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
        return escaped.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n  ", StringComparison.Ordinal);
    }
}

/// <summary>Stable, privacy-safe identity prepared by the window hit-test.</summary>
public sealed record FeedbackTargetDescriptor(string Page, string ElementId, string ElementLabel);
