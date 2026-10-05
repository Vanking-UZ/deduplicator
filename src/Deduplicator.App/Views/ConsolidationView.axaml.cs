using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class ConsolidationView : UserControl
{
    public ConsolidationView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConsolidationViewModel vm)
                vm.PickFolder = PickFolderAsync;
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = Localization.Strings.ConsolidationTarget });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }
}
