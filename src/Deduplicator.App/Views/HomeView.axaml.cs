using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Deduplicator.App.Views;

public partial class HomeView : UserControl
{
    private int _toolColumns = -1;
    private bool? _bottomStacked;
    private bool? _illustrationHidden;

    public HomeView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        double w = Bounds.Width;
        if (w <= 0) return;

        // Иллюстрация уходит первой на узких окнах — освобождает ширину для hero-текста.
        bool hideIllustration = w < 760;
        if (_illustrationHidden != hideIllustration)
        {
            _illustrationHidden = hideIllustration;
            HeroIllustration.IsVisible = !hideIllustration;
        }

        // Сетка инструментов: 3 → 2 → 1 колонки, чтобы плитки не сжимались до нечитаемости.
        int cols = w >= 940 ? 3 : w >= 640 ? 2 : 1;
        if (_toolColumns != cols)
        {
            _toolColumns = cols;
            ToolsGrid.Columns = cols;
        }

        // Нижний ряд «Последние диски + Файлы под контролем» складывается в столбик на узком окне.
        bool stacked = w < 860;
        if (_bottomStacked != stacked)
        {
            _bottomStacked = stacked;
            ApplyBottomLayout(stacked);
        }
    }

    private void ApplyBottomLayout(bool stacked)
    {
        BottomGrid.ColumnDefinitions.Clear();
        BottomGrid.RowDefinitions.Clear();

        if (stacked)
        {
            BottomGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            BottomGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            BottomGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(RecentDisksCard, 0);
            Grid.SetRow(RecentDisksCard, 0);
            Grid.SetColumn(SafetyCard, 0);
            Grid.SetRow(SafetyCard, 1);
            SafetyCard.Margin = new Thickness(0, 14, 0, 0);
            SafetyCard.Width = double.NaN;
            SafetyCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            BottomGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            BottomGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            BottomGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(RecentDisksCard, 0);
            Grid.SetRow(RecentDisksCard, 0);
            Grid.SetColumn(SafetyCard, 1);
            Grid.SetRow(SafetyCard, 0);
            SafetyCard.Margin = new Thickness(16, 0, 0, 0);
            SafetyCard.Width = 300;
        }
    }
}
