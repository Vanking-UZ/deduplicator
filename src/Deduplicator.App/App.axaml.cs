using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels;
using Deduplicator.App.Views;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;
using Microsoft.Extensions.DependencyInjection;

namespace Deduplicator.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            splash.Show();

            try
            {
                // Тяжёлая инициализация — в фоне, чтобы экран загрузки оставался отзывчивым.
                splash.SetStatus(Strings.SplashDb);
                Services = await Task.Run(Composition.Build);

                // Визуальные предпочтения применяем на UI-потоке до создания главного окна.
                var appSettings = Services.GetRequiredService<ISettingsService>().Current;
                ApplyTheme(appSettings.Theme);
                ApplyUiPreferences(appSettings.UiScale, appSettings.ReduceMotion);

                splash.SetStatus(Strings.SplashVolumes);
                var volumes = Services.GetRequiredService<IVolumeService>();
                await Task.Run(() => { try { volumes.SyncAndGetCatalog(); } catch { /* носители — не критично */ } });

                // Живое отслеживание подключения/извлечения носителей.
                Services.GetRequiredService<IDriveWatcher>().Start();

                splash.SetStatus(Strings.SplashAlmost);
                await Task.Delay(450); // мягкий минимум показа, чтобы экран не «мигал»

                var main = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainWindowViewModel>()
                };
                desktop.MainWindow = main;
                main.Show();
                splash.Close();

                // «Программа сама замечает»: сторож новых дубликатов и подсказка о носителе
                // с фотографиями показывают тосты поверх главного окна. Сбой этой обвязки
                // не должен мешать запуску — приложение полноценно работает и без неё.
                try { WireBackgroundHints(main); }
                catch (Exception ex) { AppLog.Error("WireBackgroundHints", ex); }
            }
            catch (Exception ex)
            {
                // Ранее необработанное исключение здесь роняло приложение «в рабочий стол»
                // (повреждённая БД, нет прав на запись, диск полон). Теперь — понятное окно.
                AppLog.Error("Startup", ex);
                try { splash.Close(); } catch { /* ignore */ }
                ShowStartupError(desktop, ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Подписка тостов на события сторожа (Sentinel-lite) и подсказки «носитель с фотографиями»,
    /// плюс автозапуск сторожа, если он включён в настройках. Вкл/выкл на лету делает
    /// SettingsViewModel.Save (как fpcalc/тема).
    /// </summary>
    private void WireBackgroundHints(MainWindow main)
    {
        // А. Сторож: новый файл в наблюдаемой папке оказался копией уже известного.
        var sentinel = Services.GetRequiredService<ISentinelService>();
        sentinel.DuplicateFound += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            main.ShowToast(
                "Новый дубликат: " + System.IO.Path.GetFileName(e.FilePath),
                $"Такой файл уже есть ({e.ExistingCount} шт., {Infrastructure.Format.Bytes(e.Size)})."));
        var s = Services.GetRequiredService<ISettingsService>().Current;
        if (s.SentinelEnabled && s.SentinelFolders.Count > 0)
            sentinel.Start(s.SentinelFolders);

        // Авто-проверка обновлений на GitHub (официальный репозиторий по умолчанию) в фоне —
        // не задерживает открытие окна. Обновление не устанавливается молча: показываем уведомление
        // и открываем официальный release только по клику пользователя. Выключается в настройках.
        if (s.CheckUpdatesEnabled)
            _ = CheckUpdatesOnStartupAsync(main, s.EffectiveUpdateRepository);

        // Б. Подключён носитель с папкой DCIM: тост-приглашение (клик — сразу к проверке);
        // надёжный основной путь — баннер с кнопкой на Главной (HomeViewModel).
        var home = Services.GetRequiredService<HomeViewModel>();
        home.PhotoDeviceDetected += label => // событие приходит уже на UI-потоке
            main.ShowToast(
                "Диск с фотографиями",
                $"Подключён диск с фотографиями ({label}). Нажмите, чтобы проверить на дубликаты.",
                onClick: () => home.CheckPhotoDeviceCommand.Execute(null));

        // В. Диск выдернули: успокаивающий тост — ничего не потеряно. Метку исчезнувшего
        // диска после извлечения уже не спросить, поэтому снапшот «корень → метка» ведём сами.
        // Во время ИДУЩЕГО скана тост не показываем: скан сам завершится честной карточкой ошибки,
        // и два сообщения об одном событии только путали бы.
        var scan = Services.GetRequiredService<IScanService>();
        var driveGate = new object(); // DrivesChanged приходит с таймера вотчера — не даём обработчикам гоняться
        var knownDrives = SnapshotDrives();
        Services.GetRequiredService<IDriveWatcher>().DrivesChanged += () =>
        {
            try
            {
                List<string> removed;
                lock (driveGate)
                {
                    var current = SnapshotDrives();
                    removed = knownDrives.Where(kv => !current.ContainsKey(kv.Key))
                        .Select(kv => kv.Value).ToList();
                    knownDrives = current;
                }
                if (removed.Count == 0 || scan.IsRunning) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var label in removed)
                        main.ShowToast("Диск отключён",
                            $"Диск {label} отключён. Всё в порядке: данные и результаты проверки сохранены.");
                });
            }
            catch (Exception ex) { AppLog.Error("DriveRemovedToast", ex); }
        };
    }

    private async Task CheckUpdatesOnStartupAsync(MainWindow main, string repository)
    {
        try
        {
            var update = await Services.GetRequiredService<IUpdateService>().CheckAsync(repository);
            if (update is not { IsNewer: true } ||
                !Uri.TryCreate(update.Url, UriKind.Absolute, out var releaseUri) ||
                releaseUri.Scheme != Uri.UriSchemeHttps)
                return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                main.ShowToast(
                    "Доступно обновление " + update.LatestVersion,
                    "Нажмите, чтобы открыть описание версии. Установка начнётся только после вашего подтверждения.",
                    onClick: () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(releaseUri.AbsoluteUri)
                    { UseShellExecute = true })));
        }
        catch (Exception ex) { AppLog.Error("UpdateCheck.Startup", ex); }
    }

    /// <summary>Снапшот подключённых локальных/съёмных дисков: корень → человекочитаемая метка.
    /// Сетевые не опрашиваем: синхронные SMB-запросы к полумёртвой шаре виснут (паттерн DriveWatcher).</summary>
    private static Dictionary<string, string> SnapshotDrives()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                    var root = d.RootDirectory.FullName;      // «E:\»
                    var letter = root.TrimEnd('\\', '/');     // «E:»
                    string label;
                    try { label = d.VolumeLabel; } catch { label = ""; }
                    map[root] = string.IsNullOrWhiteSpace(label) ? letter : $"«{label}» ({letter})";
                }
                catch { /* носитель извлекли между перечислением и опросом */ }
            }
        }
        catch { /* перечисление недоступно — тост просто промолчит */ }
        return map;
    }

    /// <summary>Окно с понятной ошибкой запуска вместо краха процесса.</summary>
    private static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex)
    {
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Deduplicator — ошибка запуска",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Не удалось запустить приложение.\n\n" + ex.Message +
                               "\n\nПроверьте права на папку данных и свободное место на диске. " +
                               "Подробности — в файле журнала deduplicator.log.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    close
                }
            }
        };
        close.Click += (_, _) => window.Close();
        desktop.MainWindow = window;
        window.Show();
    }

    public void ApplyTheme(string theme) => RequestedThemeVariant = theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    public void ApplyUiPreferences(double scale, bool reduceMotion)
    {
        scale = Math.Clamp(scale, 1.0, 1.5);
        Resources["UiBodyFontSize"] = 15d * scale;
        Resources["UiH1FontSize"] = 30d * scale;
        Resources["UiH2FontSize"] = 20d * scale;
        Resources["UiDisplayFontSize"] = 34d * scale;
        Resources["UiDisplayLineHeight"] = 40d * scale;
        Resources["UiSectionFontSize"] = 18d * scale;
        Resources["UiEyebrowFontSize"] = 11d * scale;
        Resources["UiBodyLargeFontSize"] = 16d * scale;
        Resources["UiBodyLineHeight"] = 24d * scale;
        Resources["UiCaptionFontSize"] = 13.5d * scale;
        Resources["UiControlMinHeight"] = 44d * scale;
        Resources["UiCheckMinHeight"] = 40d * scale;
        Resources["UiInputFontSize"] = 15d * scale;
        Resources["UiButtonPadding"] = new Thickness(16d * scale, 9d * scale);
        Infrastructure.MotionPreferences.ApplyUserPreference(reduceMotion);
    }
}
