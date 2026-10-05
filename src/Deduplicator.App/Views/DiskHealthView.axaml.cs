using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Deduplicator.App.Views;

public partial class DiskHealthView : UserControl
{
    public DiskHealthView() => AvaloniaXamlLoader.Load(this);
}
