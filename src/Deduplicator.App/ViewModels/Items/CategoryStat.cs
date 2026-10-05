using Avalonia;
using Avalonia.Media;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Строка разбивки по категориям для диаграммы статистики.</summary>
public sealed class CategoryStat
{
    public FileCategory Category { get; init; }
    public long Count { get; init; }
    public long Bytes { get; init; }
    public double Fraction { get; init; }

    public string Name => Strings.Category(Category);
    public string CountText => Format.Count(Count, "файл", "файла", "файлов");
    public string BytesText => Format.Bytes(Bytes);
    public double BarWidth => System.Math.Max(2, Fraction * 280);

    // Цвета диаграммы из ресурсов темы (раньше — хардкоды светлой палитры).
    // Строки пересоздаются при загрузке статистики — смена темы подхватывается при навигации.
    public IBrush Color => ResourceBrush(Category switch
    {
        FileCategory.Image => "AccentBrush",
        FileCategory.Audio => "SuccessBrush",
        FileCategory.Video => "WarningBrush",
        FileCategory.Document => "PurpleBrush",
        _ => "TextTertiary"
    }, "#8E8E93");

    private static IBrush ResourceBrush(string key, string fallback)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is IBrush b)
            return b;
        return new SolidColorBrush(Avalonia.Media.Color.Parse(fallback));
    }
}
