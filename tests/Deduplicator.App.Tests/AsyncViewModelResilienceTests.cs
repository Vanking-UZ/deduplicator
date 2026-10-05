using Deduplicator.App.Infrastructure;
using Deduplicator.App.ViewModels;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.Tests;

public sealed class AsyncViewModelResilienceTests
{
    [Fact]
    public async Task Statistics_load_reports_failure_and_always_leaves_busy_state()
    {
        var viewModel = new StatisticsViewModel(new FailingStatisticsService());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorText));
    }

    [Fact]
    public async Task Disk_health_refresh_reports_failure_and_always_leaves_busy_state()
    {
        var viewModel = new DiskHealthViewModel(new FailingDiskHealthService());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorText));
    }

    [Fact]
    public async Task Consolidation_source_load_reports_failure_and_always_leaves_busy_state()
    {
        var viewModel = new ConsolidationViewModel(new UnusedConsolidationService(), new FailingVolumeService());

        await viewModel.LoadVolumesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsLoadingSources);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SourceLoadError));
    }

    [Fact]
    public async Task Scan_navigation_load_observes_failure_and_clears_stale_selection()
    {
        var viewModel = new ScanViewModel(
            new IdleScanService(), new FailingVolumeService(), new TestSettingsService(),
            new UnavailableAudioService(), new NavigationService(new EmptyServiceProvider()));

        viewModel.OnNavigatedTo();
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(viewModel.EmptyHint));

        Assert.Empty(viewModel.Volumes);
        Assert.False(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Explorer_navigation_load_observes_failure_and_resets_busy_state()
    {
        string root = CreateTempRoot();
        try
        {
            var files = new FileRepository(new Database(Path.Combine(root, "catalog.db")));
            var viewModel = new ExplorerViewModel(
                new FailingVolumeService(), files, new TestSettingsService(), new FileTransferService());

            viewModel.OnNavigatedTo();
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(viewModel.OperationError));

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsRightBusy);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.Status));
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task Results_navigation_load_observes_failure_clears_results_and_resets_busy_state()
    {
        string root = CreateTempRoot();
        try
        {
            var paths = new AppPaths(root);
            var database = new Database(paths.DbPath);
            var files = new FileRepository(database);
            var groups = new GroupRepository(database);
            var sessions = new ScanSessionRepository(database);
            var hash = new HashService();
            var fileOps = new FileOpsService(
                new OperationLogRepository(database), files, groups, hash, paths.QuarantineDir);
            var viewModel = new ResultsViewModel(
                groups, new FailingVolumeService(), new ImageMatchService(paths.ThumbnailDir),
                fileOps, hash, new TestSettingsService(), new BatchRenameService(), files, paths,
                sessions, new ResultDistributionService(), new NavigationService(new EmptyServiceProvider()),
                new IdleScanService());

            viewModel.OnNavigatedTo();
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(viewModel.OperationErrorText));

            Assert.False(viewModel.IsBusy);
            Assert.Empty(viewModel.ExactGroups);
            Assert.Empty(viewModel.SimilarImageGroups);
            Assert.Empty(viewModel.SimilarAudioGroups);
        }
        finally { DeleteTempRoot(root); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 200 && !predicate(); i++)
            await Task.Delay(10);
        Assert.True(predicate(), "Фоновая загрузка не завершилась ожидаемым состоянием.");
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dedup_app_resilience_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class FailingStatisticsService : IStatisticsService
    {
        public DuplicateStatistics Compute() => throw new InvalidOperationException("test failure");
    }

    private sealed class FailingDiskHealthService : IDiskHealthService
    {
        public bool IsSupported => true;
        public List<DiskHealthInfo> GetDisks() => throw new InvalidOperationException("test failure");
    }

    private sealed class FailingVolumeService : IVolumeService
    {
        public List<VolumeRecord> GetConnectedVolumes() => throw new InvalidOperationException("test failure");
        public List<VolumeRecord> SyncAndGetCatalog() => throw new InvalidOperationException("test failure");
        public long Register(VolumeRecord volume) => throw new InvalidOperationException("test failure");
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event Action<AppSettings>? Changed;

        public void Save(AppSettings settings) => Changed?.Invoke(settings);
    }

    private sealed class UnavailableAudioService : IAudioMatchService
    {
        public bool IsAvailable => false;
        public string? FpcalcPath => null;
        public void Reconfigure(string? configuredPath) { }
        public byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default) => null;
    }

    private sealed class IdleScanService : IScanService
    {
        public ScanProgress Progress { get; } = new();
        public event Action<ScanProgress>? ProgressChanged { add { } remove { } }
        public bool IsRunning => false;
        public bool IsPaused => false;
        public long? ActiveSessionId => null;
        public bool ActiveExactReady => false;
        public Task RunAsync(ScanOptions options, CancellationToken ct) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public ScanSession? GetResumableSession() => null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class UnusedConsolidationService : IConsolidationService
    {
        public ConsolidationPlan BuildPlan(ConsolidationOptions options) =>
            throw new InvalidOperationException("not used");

        public ConsolidationReport Execute(ConsolidationPlan plan, ConsolidationOptions options,
            Action<int, int, string>? onProgress, CancellationToken ct) =>
            throw new InvalidOperationException("not used");
    }
}
