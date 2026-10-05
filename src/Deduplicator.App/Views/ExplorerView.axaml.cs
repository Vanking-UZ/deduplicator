using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Deduplicator.App.ViewModels;
using Deduplicator.App.ViewModels.Items;

namespace Deduplicator.App.Views;

public partial class ExplorerView : UserControl
{
    private ExplorerViewModel? _vm;

    public ExplorerView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PickDestinationFolder = null;
                _vm.ToastRequested -= OnToastRequested;
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.Columns.CollectionChanged -= OnColumnsChanged;
            }
            _vm = DataContext as ExplorerViewModel;
            if (_vm is not null)
            {
                _vm.PickDestinationFolder = PickFolderAsync;
                _vm.ToastRequested += OnToastRequested;
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.Columns.CollectionChanged += OnColumnsChanged;
            }
        };
    }

    /// <summary>
    /// Раскрытие/сворачивание папки двойным кликом. В Avalonia TreeView по умолчанию двойной клик
    /// по строке узел не раскрывает; делаем это явно и надёжно — управляем контейнером напрямую
    /// (локальное значение перекрывает капризы style-биндинга) и параллельно триггерим ленивую
    /// подгрузку через модель.
    /// </summary>
    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v) return;
        var item = v.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not ExplorerNode node || !node.IsFolder) return;

        bool expand = !item.IsExpanded;
        item.IsExpanded = expand; // визуальное состояние контейнера (надёжно)
        node.IsExpanded = expand; // гарантированно запускает ленивую подгрузку детей
        e.Handled = true;
    }

    /// <summary>Клик по папке в дереве — показать её содержимое в правой панели.</summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not TreeView tree) return;
        if (tree.SelectedItem is ExplorerNode { IsFolder: true } node)
            _vm.NavigateToTreeNode(node);
    }

    /// <summary>Двойной клик в правой панели: папка — провалиться, файл — открыть.</summary>
    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || e.Source is not Visual v) return;
        var item = v.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not ExplorerNode node) return;
        _vm.OpenEntry(node);
        e.Handled = true;
    }

    /// <summary>Множественное выделение в видах «значки»/«список» — отдаём его вью-модели.</summary>
    private void OnRightSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not ListBox lb) return;
        // Скрытый список (другой вид) не должен перетирать актуальное выделение.
        if (!lb.IsEffectivelyVisible) return;
        _vm.UpdateSelection(lb.SelectedItems?.OfType<ExplorerNode>() ?? Enumerable.Empty<ExplorerNode>());
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // При смене вида выделение начинается заново — чистим оба списка,
        // иначе «спрятанное» выделение всплывёт при возврате к прежнему виду.
        if (e.PropertyName == nameof(ExplorerViewModel.ViewMode))
        {
            this.FindControl<ListBox>("IconsList")?.Selection.Clear();
            this.FindControl<ListBox>("RowsList")?.Selection.Clear();
        }
        if (e.PropertyName == nameof(ExplorerViewModel.ShowTransferConfirm) && _vm?.ShowTransferConfirm == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                this.FindControl<Button>("TransferCancelButton")?.Focus());
        if (e.PropertyName == nameof(ExplorerViewModel.IsTransferring) && _vm?.IsTransferring == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                this.FindControl<Button>("TransferProgressCancelButton")?.Focus());
    }

    /// <summary>Новая колонка Миллера — прокручиваем панораму вправо, чтобы её было видно.</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var scroll = this.FindControl<ScrollViewer>("ColumnsScroll");
        if (scroll is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            scroll.Offset = new Vector(Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width), scroll.Offset.Y),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = Localization.Strings.ExplorerPickDestination });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    private void OnToastRequested(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main)
            main.ShowToast(title, message);
    }
}
