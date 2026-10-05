using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class ScanView : UserControl
{
    public ScanView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScanViewModel vm)
                vm.PickFolder = PickFolderAsync;
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Выберите папку для поиска дублей" });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }
}
