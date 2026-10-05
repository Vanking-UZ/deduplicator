using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Найденный элемент мусора в списке очистки.</summary>
public partial class JunkItemViewModel : ObservableObject
{
    public JunkItem Model { get; }
    public JunkItemViewModel(JunkItem model) => Model = model;

    [ObservableProperty] private bool _isSelected = true;

    public string Path => Model.Path;
    public string SizeText => Model.IsFolder ? "" : Format.Bytes(Model.Size);
    public string KindText => Model.Kind switch
    {
        JunkKind.EmptyFolder => Strings.JunkEmptyFolder,
        JunkKind.ZeroByteFile => Strings.JunkZeroByte,
        JunkKind.TempFile => Strings.JunkTemp,
        JunkKind.BrokenShortcut => Strings.JunkBroken,
        JunkKind.LargeFile => Strings.JunkLarge,
        _ => "—"
    };

    public Geometry? Icon => ResourceGeometry(Model.Kind switch
    {
        JunkKind.EmptyFolder => "IconFolder",
        JunkKind.BrokenShortcut => "IconWarning",
        _ => "IconFile"
    });

    private static Geometry? ResourceGeometry(string key)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is Geometry g) return g;
        return null;
    }
}
