using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Узел дерева проводника (папка или файл). Поддерживает ленивую подгрузку с диска.</summary>
public partial class ExplorerNode : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public FileCategory Category { get; init; }
    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }
    public DateTimeOffset Created { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public string? AbsolutePath { get; init; }

    public ObservableCollection<ExplorerNode> Children { get; } = new();

    private Func<ExplorerNode, Task>? _lazyLoader;
    private bool _loaded;
    private bool _isLoading;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Включить ленивую подгрузку детей. Для живой папки наличие детей можно быстро проверить в ФС;
    /// для сохранённого индекса вызывающая сторона уже знает, что папка выведена из пути потомка.
    /// </summary>
    public void EnableLazyLoading(Func<ExplorerNode, Task> loader, bool? hasChildren = null)
    {
        _lazyLoader = loader;
        _loaded = false;
        // Заглушка (и стрелка раскрытия) — только если внутри реально что-то есть.
        // Пустые и недоступные папки не показывают «ложный» треугольник.
        if (hasChildren ?? HasAnyChild())
            Children.Add(new ExplorerNode { Name = "…" });
    }

    private bool HasAnyChild()
    {
        if (AbsolutePath is null) return false;
        try
        {
            using var e = System.IO.Directory.EnumerateFileSystemEntries(
                Deduplicator.Core.Util.LongPath.Fix(AbsolutePath)).GetEnumerator();
            return e.MoveNext();
        }
        catch { return false; } // нет доступа/недоступно — без стрелки раскрытия
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && !_isLoading && _lazyLoader is not null)
        {
            _isLoading = true;
            Children.Clear();
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            await _lazyLoader!(this);
            _loaded = true;
        }
        catch
        {
            _loaded = false; // позволить повторную попытку
        }
        finally
        {
            _isLoading = false;
        }
    }

    public string SizeText => IsFolder ? "" : Format.Bytes(Size);
    /// <summary>Подпись под плиткой значков: «Папка» для каталогов, размер — для файлов.</summary>
    public string SizeOrKind => IsFolder ? "Папка" : Format.Bytes(Size);
    public string ModifiedText => IsFolder ? "" : Format.DateShort(Modified);
    public string CreatedText => IsFolder ? "" : Format.DateShort(Created == default ? Modified : Created);
    public string CategoryText => IsFolder ? "" : Strings.Category(Category);

    public Geometry? Icon => ResourceGeometry(IsFolder ? "IconFolder" : Category switch
    {
        FileCategory.Image => "IconImage",
        FileCategory.Audio => "IconMusic",
        FileCategory.Video => "IconVideo",
        FileCategory.Document => "IconDocument",
        _ => "IconFile"
    });

    /// <summary>Цвет иконки: папки — акцентные (сразу выделяются), файлы — по типу (спокойная палитра).</summary>
    public IBrush? IconBrush => ResourceBrush(IsFolder ? "AccentBrush" : Category switch
    {
        FileCategory.Image => "AccentBrush",
        FileCategory.Audio => "PurpleBrush",
        FileCategory.Video => "WarningBrush",
        FileCategory.Document => "IndigoBrush",
        _ => "TextTertiary"
    });

    private static Geometry? ResourceGeometry(string key)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is Geometry g)
            return g;
        return null;
    }

    private static IBrush? ResourceBrush(string key)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is IBrush b)
            return b;
        return null;
    }
}
