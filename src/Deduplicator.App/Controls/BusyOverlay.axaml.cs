using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Deduplicator.App.Controls;

/// <summary>
/// Единый оверлей «идёт обработка» — затемнение OverlayDim, карточка с
/// AnimatedLogo 64, заголовком, необязательным подзаголовком и необязательной
/// кнопкой «Отмена». Показ/скрытие — обычной привязкой IsVisible к флагу вьюмодели.
/// </summary>
public partial class BusyOverlay : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<BusyOverlay, string?>(nameof(Text));

    public static readonly StyledProperty<string?> SubTextProperty =
        AvaloniaProperty.Register<BusyOverlay, string?>(nameof(SubText));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<BusyOverlay, ICommand?>(nameof(CancelCommand));

    /// <summary>Заголовок карточки (например, текущий статус загрузки).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Необязательный подзаголовок (мелкий текст под заголовком).</summary>
    public string? SubText
    {
        get => GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    /// <summary>Необязательная команда отмены; если не задана — кнопки «Отмена» нет.</summary>
    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public BusyOverlay()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
