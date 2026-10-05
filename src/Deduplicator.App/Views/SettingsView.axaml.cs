using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.PickFolder = PickFolderAsync;
                vm.CopyToClipboard = CopyToClipboardAsync;
            }
        };
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not null) await top.Clipboard.SetTextAsync(text);
    }

    // #27: при очистке поля (select-all + Delete) значение не «пропадает» —
    // возвращаем прежнее число, чтобы поле не оставалось пустым и правка не терялась.
    private void OnNumericChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (sender is NumericUpDown nud && e.NewValue is null)
            nud.Value = e.OldValue ?? nud.Minimum;
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = Localization.Strings.SetQuarantine });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }
}
