using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class CleanupView : UserControl
{
    private CleanupViewModel? _vm;

    public CleanupView()
    {
        AvaloniaXamlLoader.Load(this);
        // При открытии danger-диалога удаления фокус — на безопасную кнопку «Отмена»
        // (паттерн ResultsView), чтобы случайный Enter не подтвердил удаление.
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as CleanupViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupViewModel.ShowDeleteConfirm) && _vm?.ShowDeleteConfirm == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("DeleteCancelButton")?.Focus());
    }
}
