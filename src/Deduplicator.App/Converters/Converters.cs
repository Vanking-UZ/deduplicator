using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.Converters;

/// <summary>Конвертеры значений для привязок XAML (используются через x:Static).</summary>
public static class Converters
{
    public static readonly FuncValueConverter<long, string> Bytes = new(b => Format.Bytes(b));

    public static readonly FuncValueConverter<double, string> Percent = new(Format.Percent);

    public static readonly FuncValueConverter<DateTimeOffset, string> Date = new(Format.DateShort);

    public static readonly FuncValueConverter<FileCategory, string> CategoryName = new(Strings.Category);

    public static readonly FuncValueConverter<FileCategory, Geometry?> CategoryGeometry =
        new(c => ResourceGeometry(c switch
        {
            FileCategory.Image => "IconImage",
            FileCategory.Audio => "IconMusic",
            FileCategory.Video => "IconVideo",
            FileCategory.Document => "IconDocument",
            _ => "IconFile"
        }));

    public static readonly FuncValueConverter<bool, string> ConnectionText =
        new(connected => connected ? Strings.Connected : Strings.Disconnected);

    public static readonly FuncValueConverter<string?, Geometry?> Icon =
        new(key => key is null ? null : ResourceGeometry(key));

    public static readonly FuncValueConverter<bool, bool> Not = new(b => !b);

    public static readonly FuncValueConverter<string?, bool> IsNotEmpty =
        new(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>true→1.0, false→0.0 — плавное появление оверлеев: Opacity привязывают
    /// к тому же флагу, что и IsVisible, и добавляют DoubleTransition (150 мс).</summary>
    public static readonly FuncValueConverter<bool, double> ToOpacity = new(b => b ? 1.0 : 0.0);

    private static Geometry? ResourceGeometry(string key)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is Geometry g)
            return g;
        return null;
    }
}
