using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

/// <summary>
/// 2.8 «Прогрессивные результаты»: на долгом скане точные группы должны быть видны по
/// <see cref="IScanService.ActiveSessionId"/> сразу после того, как exact-движок отработал
/// (<see cref="IScanService.ActiveExactReady"/>), а не после полного завершения скана.
/// Этап похожих удерживается ДЕТЕРМИНИРОВАННО фейком аудио-сервиса (гейт вместо таймингов).
/// Безопасность: отмена после ActiveExactReady по-прежнему снимает группы текущей сессии
/// (частичные результаты не публикуются), прежняя завершённая сессия остаётся нетронутой.
/// </summary>
public class Sprint17ProgressiveTests : IDisposable
{
    private static readonly MatchType[] ExactTypes = { MatchType.Exact, MatchType.ExactVideo };

    private readonly string _root;

    public Sprint17ProgressiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dedup_s17_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Фейк аудио-сервиса: ComputeFingerprint блокируется на гейте, пока тест не вызовет
    /// <see cref="Release"/>. Так этап «похожие» идёт ровно столько, сколько нужно тесту, —
    /// без гонок на таймингах. Отмена (ct) пробрасывается наружу, как у настоящего fpcalc.
    /// </summary>
    private sealed class GatedAudioService : IAudioMatchService
    {
        private readonly SemaphoreSlim _gate = new(0);
        public bool IsAvailable => true;
        public string? FpcalcPath => null;
        public void Reconfigure(string? configuredPath) { }
        public void Release(int count = 1000) => _gate.Release(count);

        public byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default)
        {
            _gate.Wait(ct);
            return null; // отпечаток «не удался» — на следующем скане файл снова требует расчёта
        }
    }

    private sealed record Ctx(
        ScanService Scan, GroupRepository Groups, ScanSessionRepository Sessions,
        GatedAudioService Audio, string Corpus);

    /// <summary>Корпус: пара точных дублей + «аудио»-файлы, на которых фейк удержит этап похожих.</summary>
    private Ctx Build(string name)
    {
        var corpus = Path.Combine(_root, name + "_corpus");
        Directory.CreateDirectory(corpus);

        var data = new byte[60_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(corpus, "exact1.bin"), data);
        File.WriteAllBytes(Path.Combine(corpus, "exact2.bin"), (byte[])data.Clone());

        // Категория Audio определяется по расширению; содержимое не парсится — отпечаток «считает» фейк.
        var a = new byte[30_000]; new Random(7).NextBytes(a);
        File.WriteAllBytes(Path.Combine(corpus, "song_a.mp3"), a);
        var b = new byte[31_000]; new Random(8).NextBytes(b);
        File.WriteAllBytes(Path.Combine(corpus, "song_b.mp3"), b);

        var db = new Database(Path.Combine(_root, name + ".db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_root, name + "_app")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_root, name + "_thumbs"), rotationInvariant: false);
        var audio = new GatedAudioService();
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper,
            settings, new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));
        return new Ctx(scan, groups, sessions, audio, corpus);
    }

    private static ScanOptions Options(string corpus) => new()
    {
        Folders = { corpus },
        Depth = ScanDepth.WithSimilarImagesAndAudio, // этап похожих обязателен — его держит гейт
        Parallelism = 2,
        Incremental = true,
        SkipHiddenSystem = false
    };

    /// <summary>Поллинг с таймаутом (флаги выставляются фоновым потоком скана).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"не дождались: {what}");
            await Task.Delay(10);
        }
    }

    // ---------- (а) точные группы видны по активной сессии ДО завершения скана ----------

    [Fact]
    public async Task Exact_groups_readable_by_active_session_before_scan_completes()
    {
        var ctx = Build("preview");
        var task = ctx.Scan.RunAsync(Options(ctx.Corpus), default);

        await WaitUntilAsync(() => ctx.Scan.ActiveExactReady, "ActiveExactReady");

        // Скан ещё идёт: этап похожих заблокирован гейтом фейка.
        Assert.False(task.IsCompleted);
        Assert.True(ctx.Scan.IsRunning);
        long? active = ctx.Scan.ActiveSessionId;
        Assert.NotNull(active);
        long sid = active!.Value;

        // Точные группы читаются по АКТИВНОЙ сессии — та самая страница, что читает предпросмотр UI.
        Assert.True(ctx.Groups.GetGroupCount(ExactTypes, sid) >= 1);
        var page = ctx.Groups.GetGroupsPage(ExactTypes, sid, 0, 10);
        var names = page.SelectMany(g => g.Members.Select(m => m.File!.Name)).ToList();
        Assert.Contains("exact1.bin", names);
        Assert.Contains("exact2.bin", names);

        // Завершённой сессии ещё нет — обычный (не предпросмотрный) путь UI показал бы пустоту.
        Assert.Null(ctx.Sessions.GetLatestCompletedSessionId());

        // Подсказка пользователю на карточке скана (ScanView показывает Progress.Message).
        Assert.Contains("уже можно посмотреть", ctx.Scan.Progress.Message ?? "");

        // (б) после завершения предпросмотрные указатели сброшены, сессия стала завершённой.
        ctx.Audio.Release();
        await task;
        Assert.Equal(ScanStage.Completed, ctx.Scan.Progress.Stage);
        Assert.Null(ctx.Scan.ActiveSessionId);
        Assert.False(ctx.Scan.ActiveExactReady);
        Assert.Equal(sid, ctx.Sessions.GetLatestCompletedSessionId());
        Assert.True(ctx.Groups.GetGroupCount(ExactTypes, sid) >= 1); // группы «повысились» до финальных

        ctx.Scan.Dispose();
    }

    // ---------- (в) отмена после ActiveExactReady снимает группы сессии ----------

    [Fact]
    public async Task Cancel_after_exact_ready_removes_partial_groups()
    {
        var ctx = Build("cancel");
        using var cts = new CancellationTokenSource();
        var task = ctx.Scan.RunAsync(Options(ctx.Corpus), cts.Token);

        await WaitUntilAsync(() => ctx.Scan.ActiveExactReady, "ActiveExactReady");
        long sid = ctx.Scan.ActiveSessionId!.Value;
        Assert.True(ctx.Groups.GetGroupCount(ExactTypes, sid) >= 1); // предпросмотр был доступен

        cts.Cancel();
        await task; // RunCore ловит OperationCanceledException сам — задача завершается штатно

        Assert.Equal(ScanStage.Cancelled, ctx.Scan.Progress.Stage);
        // Семантика ClearGroupsInSession сохранена: частичные группы отменённой сессии сняты,
        // предпросмотру (и тем более удалению) показывать больше нечего.
        Assert.Equal(0, ctx.Groups.GetGroupCount(ExactTypes, sid));
        Assert.Empty(ctx.Groups.GetGroupsPage(ExactTypes, sid, 0, 10));
        Assert.Null(ctx.Scan.ActiveSessionId);
        Assert.False(ctx.Scan.ActiveExactReady);
        Assert.Null(ctx.Sessions.GetLatestCompletedSessionId()); // завершённой как не было, так и нет

        ctx.Scan.Dispose();
    }

    // ---------- отмена предпросмотра НЕ трогает прежнюю завершённую сессию ----------

    [Fact]
    public async Task Cancelled_preview_preserves_previous_completed_session()
    {
        var ctx = Build("preserve");

        // Скан 1: гейт отпущен ровно на два аудиофайла — завершается без удержания.
        ctx.Audio.Release(2);
        await ctx.Scan.RunAsync(Options(ctx.Corpus), default);
        Assert.Equal(ScanStage.Completed, ctx.Scan.Progress.Stage);
        long sid1 = ctx.Sessions.GetLatestCompletedSessionId()!.Value;
        int exactBefore = ctx.Groups.GetGroupCount(ExactTypes, sid1);
        Assert.True(exactBefore >= 1);

        // Скан 2: удерживается на этапе похожих (отпечатки не посчитались — файлы снова в очереди),
        // после ActiveExactReady отменяется.
        using var cts = new CancellationTokenSource();
        var task2 = ctx.Scan.RunAsync(Options(ctx.Corpus), cts.Token);
        await WaitUntilAsync(() => ctx.Scan.ActiveExactReady, "ActiveExactReady (скан 2)");
        long sid2 = ctx.Scan.ActiveSessionId!.Value;
        Assert.NotEqual(sid1, sid2);
        cts.Cancel();
        await task2;

        Assert.Equal(ScanStage.Cancelled, ctx.Scan.Progress.Stage);
        Assert.Equal(0, ctx.Groups.GetGroupCount(ExactTypes, sid2));           // частичные сняты
        Assert.Equal(exactBefore, ctx.Groups.GetGroupCount(ExactTypes, sid1)); // прежний результат цел
        Assert.Equal(sid1, ctx.Sessions.GetLatestCompletedSessionId());        // UI откатится к нему

        ctx.Scan.Dispose();
    }
}
