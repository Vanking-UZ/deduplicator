using System.Reflection;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.ViewModels;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deduplicator.App;

/// <summary>Композиционный корень: регистрация всех сервисов и вью-моделей.</summary>
public static class Composition
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        var paths = new AppPaths();

        // Логирование доступно с самого начала (NFR-1): путь резервируется в AppPaths.
        Core.Util.AppLog.Configure(paths.LogPath);
        // Стартовый баннер диагностики (ТЗ MVP): по нему видно версию приложения/движка, права и пути.
        var appVer = System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "?";
        Core.Util.AppLog.Write("================ Запуск Deduplicator ================");
        Core.Util.AppLog.Write($"App: Deduplicator {appVer}");
        Core.Util.AppLog.Write($"Engine: {Core.Engine.EngineBuildInfo.Summary}");
        Core.Util.AppLog.Write($"IsAdmin={FastDedup.Engine.EngineEnvironment.IsElevated()} " +
                               $"OS={System.Environment.OSVersion} LogPath={paths.LogPath} DbPath={paths.DbPath}");

        var database = new Database(paths.DbPath);
        database.Initialize();

        // Восстановление после сбоя: удаляем «осиротевшие» временные файлы операций и кэша.
        Core.Util.SafeIo.CleanupTempFiles(paths.QuarantineDir);
        Core.Util.SafeIo.CleanupTempFiles(paths.ThumbnailDir);

        // B1: незавершённые групповые переименования (kill/потеря питания посреди операции) —
        // возвращаем файлам исходные имена по write-ahead журналу.
        try { RenameJournal.RecoverAll(System.IO.Path.Combine(paths.RootDir, "recovery")); }
        catch (Exception ex) { Core.Util.AppLog.Error("RenameJournal.RecoverAll", ex); }

        // P0.2: после краша/убийства приложения помечаем незавершённые сессии как Failed и убираем их
        // частичные группы — чтобы они не пережили рестарт и не показывались как готовый результат.
        try
        {
            new Core.Data.ScanSessionRepository(database).MarkUnfinishedAsFailed();
            new Core.Data.GroupRepository(database).ClearGroupsNotInCompletedSessions();
            // Области сканов живут только во время активного скана — остатки после краша вычищаем,
            // иначе scan_scope_files растёт неограниченно.
            new Core.Data.FileRepository(database).ClearAllScopes();
        }
        catch (Exception ex) { Core.Util.AppLog.Error("StartupSessionCleanup", ex); }

        services.AddSingleton(paths);
        services.AddSingleton(database);

        // Репозитории
        services.AddSingleton<VolumeRepository>();
        services.AddSingleton<FileRepository>();
        services.AddSingleton<GroupRepository>();
        services.AddSingleton<LshRepository>();
        services.AddSingleton<OperationLogRepository>();
        services.AddSingleton<ScanSessionRepository>();
        services.AddSingleton<VolumeScanStateRepository>();

        // Настройки
        services.AddSingleton<ISettingsService, SettingsService>();

        // Сервисы ядра
        services.AddSingleton<IVolumeService, VolumeService>();
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IImageMatchService>(sp =>
            new ImageMatchService(
                sp.GetRequiredService<AppPaths>().ThumbnailDir,
                sp.GetRequiredService<ISettingsService>().Current.RotationInvariantImages));
        services.AddSingleton<IAudioMatchService>(sp =>
            new AudioMatchService(sp.GetRequiredService<ISettingsService>().Current.FpcalcPath));
        services.AddSingleton<IDuplicateGrouper, DuplicateGrouper>();
        // Отдельный helper создаётся лениво на один явно запущенный скан; регистрация сама UAC не показывает.
        services.AddSingleton<FastDedup.Engine.IElevatedMftBrokerFactory,
            FastDedup.Engine.ElevatedMftBrokerFactory>();
        // Быстрый энумератор файлов (USN/MFT) — источник файлов для ScanService.
        services.AddSingleton<FastDedup.Engine.FastFileLister>();
        services.AddSingleton<IScanService, ScanService>();
        services.AddSingleton<IConsolidationService, ConsolidationService>();
        services.AddSingleton<IBatchRenameService>(sp => new BatchRenameService(
            System.IO.Path.Combine(sp.GetRequiredService<AppPaths>().RootDir, "recovery"))); // P0-B + журнал B1
        services.AddSingleton<IResultDistributionService, ResultDistributionService>(); // P0-D
        services.AddSingleton<IFileTransferService, FileTransferService>(); // Перенос/копирование в проводнике
        services.AddSingleton<IStatisticsService, StatisticsService>();
        services.AddSingleton<IDiskHealthService, DiskHealthService>();
        services.AddSingleton<IJunkScanService, JunkScanService>();
        services.AddSingleton<IDriveWatcher, DriveWatcher>();
        // Сторожевой режим (Sentinel-lite): следит за новыми дубликатами, пока приложение открыто.
        services.AddSingleton<ISentinelService, SentinelService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IFileOpsService>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            var appPaths = sp.GetRequiredService<AppPaths>();
            return new FileOpsService(
                sp.GetRequiredService<OperationLogRepository>(),
                sp.GetRequiredService<FileRepository>(),
                sp.GetRequiredService<GroupRepository>(),
                sp.GetRequiredService<IHashService>(),
                settings.Current.QuarantinePath ?? appPaths.QuarantineDir);
        });

        // Навигация и вью-модели (страницы — синглтоны, чтобы хранить состояние)
        services.AddSingleton<NavigationService>();
        services.AddSingleton<IUiFeedbackDraftStore, UiFeedbackDraftStore>();
        services.AddSingleton<FeedbackPickerViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<VolumesViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<ScanViewModel>();
        services.AddSingleton<ResultsViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<ConsolidationViewModel>();
        services.AddSingleton<JournalViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiskHealthViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<CrossDiskViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
