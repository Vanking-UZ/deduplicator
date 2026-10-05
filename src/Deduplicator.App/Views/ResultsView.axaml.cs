using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Deduplicator.App.ViewModels;
using Deduplicator.App.ViewModels.Items;

namespace Deduplicator.App.Views;

public partial class ResultsView : UserControl
{
    private ResultsViewModel? _vm;

    public ResultsView()
    {
        AvaloniaXamlLoader.Load(this);
        // Клавиши просмотрщика (←/→) ловим ТУННЕЛЕМ на корне вьюхи: KeyBinding на UserControl
        // не срабатывает без фокуса внутри него, а bubbling могли бы съесть вложенные контролы.
        // Esc здесь больше не обрабатываем — его глобально ловит MainWindow и зовёт
        // CloseDialog() (просмотрщик входит в HasOpenDialog), иначе обработка задваивалась бы.
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as ResultsViewModel;
            if (_vm is not null)
            {
                _vm.PickSaveFile = PickSaveFileAsync;
                _vm.PickDestinationFolder = PickFolderAsync;
                _vm.PropertyChanged += OnVmPropertyChanged;
            }
        };
    }

    /// <summary>
    /// При открытии просмотрщика переносим фокус на его оверлей: иначе KeyDown маршрутизируется
    /// от элемента, имевшего фокус ДО открытия (он может быть вне этой вьюхи — например, после
    /// двойного клика по плитке фокус вообще не менялся), и стрелки/Esc не доходили бы до нас.
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.ShowViewer) && _vm?.ShowViewer == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Border>("ViewerOverlay")?.Focus());
        // Danger-диалог удаления открылся — фокус на БЕЗОПАСНУЮ кнопку «Отмена»,
        // чтобы случайный Enter не подтвердил удаление.
        if (e.PropertyName == nameof(ResultsViewModel.ShowDeleteConfirm) && _vm?.ShowDeleteConfirm == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("DeleteCancelButton")?.Focus());
        if (e.PropertyName == nameof(ResultsViewModel.ShowRenameDialog) && _vm?.ShowRenameDialog == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("RenameCancelButton")?.Focus());
        if (e.PropertyName == nameof(ResultsViewModel.ShowDistributeDialog) && _vm?.ShowDistributeDialog == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("DistributeCancelButton")?.Focus());
        if (e.PropertyName == nameof(ResultsViewModel.ShowHardlinkConfirm) && _vm?.ShowHardlinkConfirm == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("HardlinkCancelButton")?.Focus());
        if (e.PropertyName == nameof(ResultsViewModel.IsBusy) && _vm?.IsBusy == true)
            Dispatcher.UIThread.Post(() => this.FindControl<Control>("ResultsBusyOverlay")?.Focus());
    }

    /// <summary>
    /// Двойной клик по плитке файла открывает просмотрщик сравнения
    /// (паттерн ExplorerView.OnNodeDoubleTapped). Двойные клики по чекбоксу отметки и кнопкам
    /// плитки игнорируем — у них своё действие, случайное открытие только мешало бы.
    /// </summary>
    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ResultsViewModel vm) return;
        if (sender is not Control c || c.DataContext is not FileItemViewModel file) return;
        if (e.Source is Visual v &&
            (v.FindAncestorOfType<CheckBox>(includeSelf: true) is not null ||
             v.FindAncestorOfType<Button>(includeSelf: true) is not null)) return;
        vm.OpenViewerCommand.Execute(file);
        e.Handled = true;
    }

    /// <summary>Клавиатура просмотрщика: ←/→ — листание не-эталонных файлов
    /// (Esc — глобально в MainWindow, см. комментарий в конструкторе).</summary>
    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { ShowViewer: true } vm) return;
        switch (e.Key)
        {
            case Key.Left: vm.ViewerPrevCommand.Execute(null); e.Handled = true; break;
            case Key.Right: vm.ViewerNextCommand.Execute(null); e.Handled = true; break;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Папка назначения" });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string suggested)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggested,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        return file?.TryGetLocalPath();
    }
}
