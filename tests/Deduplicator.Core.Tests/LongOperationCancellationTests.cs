using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

public sealed class LongOperationCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dedup_cancel_" + Guid.NewGuid().ToString("N"));

    public LongOperationCancellationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Distribution_report_is_atomic_when_cancelled()
    {
        string output = Path.Combine(_root, "out");
        var request = new DistributionRequest(
            Enumerable.Range(1, 10).Select(i =>
                new DistributionFile(i, Path.Combine(_root, $"source-{i}.bin"), "Other", 1, 1)).ToList(),
            output, DistributionMode.ReportOnly);
        var service = new ResultDistributionService();
        var plan = service.BuildPlan(request);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => service.Execute(plan, request, cts.Token));
        Assert.False(File.Exists(Path.Combine(output, "distribution-report.csv")));
        Assert.Empty(Directory.Exists(output)
            ? Directory.EnumerateFiles(output, "*.tmp", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>());
    }

    [Fact]
    public void Distribution_reports_bounded_progress_while_streaming()
    {
        string output = Path.Combine(_root, "progress");
        var request = new DistributionRequest(
            Enumerable.Range(1, 3).Select(i =>
                new DistributionFile(i, Path.Combine(_root, $"item-{i}.bin"), "Other", 1, 1)).ToList(),
            output, DistributionMode.ReportOnly);
        var service = new ResultDistributionService();
        var plan = service.BuildPlan(request);
        TransferProgress? last = null;

        var result = service.Execute(plan, request, default,
            new InlineProgress<TransferProgress>(p => last = p));

        Assert.Null(result.Error);
        Assert.NotNull(last);
        Assert.Equal(3, last!.Done);
        Assert.Equal(3, last.Total);
    }

    [Fact]
    public void Batch_rename_cancel_before_first_move_preserves_original_names()
    {
        string file = Path.Combine(_root, "original.txt");
        File.WriteAllText(file, "safe");
        var service = new BatchRenameService(Path.Combine(_root, "journal"));
        var plan = service.BuildPlan(new BatchRenameRequest(
            new[] { new RenameSourceFile(1, file, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 4, true) },
            "renamed", 1, 2, RenameSortOrder.AsIs));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = service.Execute(plan, cts.Token);

        Assert.True(result.RolledBack);
        Assert.Contains("остановлена", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(file));
        Assert.Equal("safe", File.ReadAllText(file));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
