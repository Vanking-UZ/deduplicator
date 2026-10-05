using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>
/// One locally stored note about a UI element.  The visual reference exists only
/// while the current window is alive and is never written to the feedback draft.
/// </summary>
public sealed partial class FeedbackSelectionItem : ObservableObject
{
    public FeedbackSelectionItem(string page, string elementId, string elementLabel, string comment = "")
    {
        Page = page;
        ElementId = elementId;
        ElementLabel = elementLabel;
        _comment = comment;
    }

    public string Page { get; }
    public string ElementId { get; }
    public string ElementLabel { get; }

    [ObservableProperty] private int _number;
    [ObservableProperty] private string _comment;

    // Coordinates are presentation-only.  They are refreshed from the live
    // Avalonia visual tree and are deliberately excluded from the local draft.
    [ObservableProperty] private bool _isOutlineVisible;
    [ObservableProperty] private double _outlineX;
    [ObservableProperty] private double _outlineY;
    [ObservableProperty] private double _outlineWidth;
    [ObservableProperty] private double _outlineHeight;

    internal WeakReference<Control>? Target { get; set; }

    public string Title => $"{Number}. {ElementLabel}";

    partial void OnNumberChanged(int value) => OnPropertyChanged(nameof(Title));
}
