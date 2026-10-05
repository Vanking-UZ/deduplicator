using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Deduplicator.App.Views;

namespace Deduplicator.App.Tests;

public sealed class ViewSmokeTests
{
    private static IEnumerable<(string Name, Func<Control> Factory)> Views()
    {
        yield return ("Главная", () => new HomeView());
        yield return ("Сканирование", () => new ScanView());
        yield return ("Результаты", () => new ResultsView());
        yield return ("Диски", () => new VolumesView());
        yield return ("Проводник", () => new ExplorerView());
        yield return ("Очистка", () => new CleanupView());
        yield return ("Сравнение дисков", () => new CrossDiskView());
        yield return ("Консолидация", () => new ConsolidationView());
        yield return ("Журнал", () => new JournalView());
        yield return ("Статистика", () => new StatisticsView());
        yield return ("Здоровье дисков", () => new DiskHealthView());
        yield return ("Настройки", () => new SettingsView());
    }

    [AvaloniaFact]
    public void All_views_load_and_lay_out_in_compact_window()
    {
        foreach (var (name, factory) in Views())
        {
            var view = factory();
            var window = new Window { Width = 640, Height = 400, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.True(view.Bounds.Width > 0, $"{name}: ширина не рассчитана");
                Assert.True(view.Bounds.Height > 0, $"{name}: высота не рассчитана");
                Assert.True(double.IsFinite(view.Bounds.Width) && double.IsFinite(view.Bounds.Height),
                    $"{name}: некорректная геометрия");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Theme_scale_and_reduced_motion_preferences_apply_without_restart()
    {
        var app = Assert.IsType<App>(Application.Current);
        app.ApplyTheme("Dark");
        app.ApplyUiPreferences(1.25, reduceMotion: true);

        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(37.5d, Assert.IsType<double>(app.Resources["UiH1FontSize"]));
        Assert.Equal(18.75d, Assert.IsType<double>(app.Resources["UiBodyFontSize"]));
        Assert.Equal(55d, Assert.IsType<double>(app.Resources["UiControlMinHeight"]));
    }

    [AvaloniaFact]
    public void Main_window_shell_loads_at_supported_minimum_size()
    {
        var window = new MainWindow { Width = 640, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ClientSize.Width >= 600);
            Assert.True(window.ClientSize.Height >= 360);
        }
        finally { window.Close(); }
    }
}
