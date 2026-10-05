using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class VolumesView : UserControl
{
    private VolumesViewModel? _vm;

    public VolumesView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as VolumesViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VolumesViewModel.ShowRemoveConfirm) && _vm?.ShowRemoveConfirm == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("RemoveCancelButton")?.Focus());
    }
}
