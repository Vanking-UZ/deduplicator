using System.Runtime.InteropServices;
using Avalonia;
using Deduplicator.Core.Util;

namespace Deduplicator.App;

internal static class Program
{
    // Держим mutex живым всё время работы процесса (иначе GC отпустит и защита исчезнет).
    private static Mutex? _singleInstanceMutex;

    // Не использовать Avalonia/сторонние API до вызова BuildAvaloniaApp.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Error("AppDomain.UnhandledException", ex);
            else AppLog.Write($"FATAL AppDomain.UnhandledException: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Один экземпляр на машину: стартовая чистка второго экземпляра (MarkUnfinishedAsFailed +
        // ClearGroupsNotInCompletedSessions) помечала АКТИВНУЮ сессию первого как Failed и удаляла
        // группы идущего скана — тот завершался «успехом» с неполными результатами.
        // Local\ не требует SeCreateGlobalPrivilege: обычный пользователь после перехода на
        // asInvoker всегда может запустить приложение. Одного экземпляра на интерактивный сеанс достаточно.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Deduplicator.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBoxW(IntPtr.Zero,
                "Deduplicator уже запущен.\nПереключитесь на открытое окно программы.",
                "Deduplicator", 0x00000040 /* MB_ICONINFORMATION */);
            return;
        }

        // Смоук-режим (DEDUP_SMOKE=1, см. MainWindow.RunSmokeAsync): Avalonia пишет предупреждения
        // (в т.ч. binding-ошибки) через LogToTrace, но без слушателя в консоли их не видно —
        // добавляем, чтобы критерий «0 binding-ошибок» был проверяемым по stdout.
        if (Environment.GetEnvironmentVariable("DEDUP_SMOKE") == "1")
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.MarkCleanShutdown();
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
