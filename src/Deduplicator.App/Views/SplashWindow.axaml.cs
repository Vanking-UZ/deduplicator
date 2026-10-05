using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Deduplicator.App.Views;

public partial class SplashWindow : Window
{
    private TextBlock? _status;

    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _status = this.FindControl<TextBlock>("StatusText");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Opacity = 1; // плавное появление через Transitions
    }

    public void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }
}
