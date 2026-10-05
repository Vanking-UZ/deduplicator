using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Deduplicator.App.Views;

public partial class JournalView : UserControl
{
    public JournalView() => AvaloniaXamlLoader.Load(this);
}
