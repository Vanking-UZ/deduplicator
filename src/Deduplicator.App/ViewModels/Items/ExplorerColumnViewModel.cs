using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>
/// Одна колонка вида «Столбцы» (Miller columns, как в Finder): содержимое папки одного уровня.
/// Выбор папки в колонке открывает следующую колонку правее, выбор файла — карточку сведений.
/// </summary>
public partial class ExplorerColumnViewModel : ObservableObject
{
    public ExplorerColumnViewModel(ExplorerCrumb crumb) => Crumb = crumb;

    /// <summary>Папка, содержимое которой показывает колонка.</summary>
    public ExplorerCrumb Crumb { get; }

    public ObservableCollection<ExplorerNode> Items { get; } = new();

    [ObservableProperty] private ExplorerNode? _selected;

    /// <summary>Родительская вью-модель узнаёт о выборе через колбэк (проще, чем события в XAML).</summary>
    internal Action<ExplorerColumnViewModel, ExplorerNode?>? SelectionCallback { get; set; }

    partial void OnSelectedChanged(ExplorerNode? value) => SelectionCallback?.Invoke(this, value);
}
