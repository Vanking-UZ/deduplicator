using System.Runtime.InteropServices;
using System.Diagnostics;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IJunkScanService
{
    List<JunkItem> Scan(JunkScanOptions options, Action<JunkScanProgress>? onProgress, CancellationToken ct);
}

public sealed record JunkScanProgress(
    long FilesVisited,
    long BytesVisited,
    int ItemsFound,
    int RootsCompleted,
    int TotalRoots,
    double? Fraction,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining);

/// <summary>Поиск «мусора»: пустые папки, файлы нулевого размера, временные файлы, битые ярлыки, большие файлы.</summary>
public sealed class JunkScanService : IJunkScanService
{
    private static readonly HashSet<string> TempExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".bak", ".old", ".crdownload", ".part", ".partial", ".download", ".~tmp", ".chk"
    };
    private static readonly HashSet<string> TempNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", ".ds_store", "desktop.ini.tmp"
    };

    private const int MaxItems = 50_000; // защита от переполнения памяти/UI на огромных дисках

    public List<JunkItem> Scan(JunkScanOptions options, Action<JunkScanProgress>? onProgress, CancellationToken ct)
    {
        var items = new List<JunkItem>();
        var emptyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var progress = new ProgressState(options.Roots, onProgress);

        // Один COM-объект WScript.Shell на весь скан (создавать на каждый .lnk нельзя — утечка/крах).
        object? shell = options.BrokenShortcuts ? CreateShell() : null;
        try
        {
            foreach (var root in options.Roots)
            {
                ct.ThrowIfCancellationRequested();
                Walk(root, options, items, emptyDirs, shell, progress, ct);
                progress.RootCompleted(items.Count);
            }
        }
        finally
        {
            if (shell is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        if (options.EmptyFolders)
        {
            // Отбираем только верхние пустые папки (родитель не пуст / отсутствует).
            foreach (var d in emptyDirs)
            {
                if (items.Count >= MaxItems) break;
                var parent = Directory.GetParent(d.TrimEnd('\\', '/'))?.FullName;
                if (parent is null || !emptyDirs.Contains(parent))
                {
                    DateTimeOffset mtime = default;
                    try { mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(LongPath.Fix(d)), TimeSpan.Zero); }
                    catch { }
                    items.Add(new JunkItem { Kind = JunkKind.EmptyFolder, Path = d, IsFolder = true, Modified = mtime });
                }
            }
            progress.Report(items.Count, force: true);
        }

        progress.Finish(items.Count);

        return items;
    }

    /// <summary>Кадр обхода каталога (для итеративного DFS вместо рекурсии).</summary>
    private sealed class DirFrame
    {
        public string Dir = "";
        public DirectoryInfo[] Subdirs = Array.Empty<DirectoryInfo>();
        public int Index;
        public bool HasFiles;
    }

    /// <summary>
    /// Итеративный обход дерева (без рекурсии — нет риска переполнения стека на очень глубоких
    /// деревьях; StackOverflowException не ловится try/catch и ронял бы всё приложение, #8).
    /// Пустой каталог определяется по принципу пост-обхода: поддерево без единого файла.
    /// </summary>
    private void Walk(string root, JunkScanOptions o, List<JunkItem> items,
        HashSet<string> emptyDirs, object? shell, ProgressState progress, CancellationToken ct)
    {
        var rootFrame = MakeFrame(root, o, items, shell, progress, ct);
        if (rootFrame is null) return; // корень недоступен

        var stack = new Stack<DirFrame>();
        stack.Push(rootFrame);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= MaxItems) break; // лимит — мягко сворачиваем обход

            var top = stack.Peek();
            if (top.Index < top.Subdirs.Length)
            {
                var sub = top.Subdirs[top.Index++];
                try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { top.HasFiles = true; continue; } }
                catch { continue; }

                var child = MakeFrame(Path.Combine(top.Dir, sub.Name), o, items, shell, progress, ct);
                if (child is null) { top.HasFiles = true; continue; } // нет доступа — родитель не пуст
                stack.Push(child);
            }
            else
            {
                stack.Pop();
                if (!top.HasFiles && o.EmptyFolders)
                {
                    if (emptyDirs.Count < MaxItems) emptyDirs.Add(top.Dir);
                    else top.HasFiles = true; // кап памяти: не считаем предков «пустыми» по неполному набору
                }
                if (stack.Count > 0 && top.HasFiles) stack.Peek().HasFiles = true;
            }
        }
    }

    /// <summary>
    /// Обрабатывает файлы одного каталога (мусор + признак «есть файлы») и возвращает кадр обхода.
    /// null — каталог недоступен (родитель пометит себя непустым, чтобы не считать ошибочно пустым).
    /// </summary>
    private DirFrame? MakeFrame(string dir, JunkScanOptions o, List<JunkItem> items,
        object? shell, ProgressState progress, CancellationToken ct)
    {
        FileInfo[] files;
        DirectoryInfo[] subdirs;
        try
        {
            var di = new DirectoryInfo(LongPath.Fix(dir));
            files = di.GetFiles();
            subdirs = di.GetDirectories();
        }
        catch
        {
            return null;
        }

        bool hasFiles = false;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= MaxItems) { hasFiles = true; break; }
            // Любая ошибка по конкретному файлу (исчез, занят, длинный путь) не должна валить скан.
            try
            {
                hasFiles = true;
                progress.VisitFile(Math.Max(0, f.Length), items.Count);
                if (o.SkipHiddenSystem && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                var full = Path.Combine(dir, f.Name);
                // Один физический путь должен появляться ровно один раз. Большой файл всегда
                // получает самый осторожный тип: он не попадёт в массовый выбор «мусора», даже
                // если одновременно имеет расширение .tmp или нулевой размер.
                JunkKind? kind = null;
                if (o.LargeFiles && f.Length >= o.LargeThresholdBytes)
                    kind = JunkKind.LargeFile;
                else if (o.TempFiles && IsTemp(f))
                    kind = JunkKind.TempFile;
                else if (o.ZeroByteFiles && f.Length == 0)
                    kind = JunkKind.ZeroByteFile;
                else if (o.BrokenShortcuts && shell is not null &&
                         f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                         IsBrokenShortcut(shell, full))
                    kind = JunkKind.BrokenShortcut;

                if (kind is { } detectedKind)
                    Add(items, detectedKind, full, f);
            }
            catch (OperationCanceledException) { throw; }
            catch { hasFiles = true; /* проблемный файл — пропускаем */ }
        }

        progress.Report(items.Count);
        return new DirFrame { Dir = dir, Subdirs = subdirs, HasFiles = hasFiles };
    }

    private sealed class ProgressState
    {
        private readonly Action<JunkScanProgress>? _callback;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _estimatedBytes;
        private readonly int _totalRoots;
        private long _files;
        private long _bytes;
        private int _rootsCompleted;
        private long _lastReportedFiles;
        private TimeSpan _lastReportedAt;

        public ProgressState(IReadOnlyCollection<string> roots, Action<JunkScanProgress>? callback)
        {
            _callback = callback;
            _totalRoots = Math.Max(1, roots.Count);
            _estimatedBytes = roots.Select(EstimateUsedBytes).Where(v => v > 0).Sum();
        }

        public void VisitFile(long bytes, int itemsFound)
        {
            _files++;
            _bytes += bytes;
            if (_files - _lastReportedFiles >= 200) Report(itemsFound);
        }

        public void RootCompleted(int itemsFound)
        {
            _rootsCompleted++;
            Report(itemsFound, force: true);
        }

        public void Finish(int itemsFound)
        {
            _rootsCompleted = _totalRoots;
            Emit(itemsFound, 1.0);
        }

        public void Report(int itemsFound, bool force = false)
        {
            if (_callback is null) return;
            var now = _clock.Elapsed;
            if (!force && _files == _lastReportedFiles && now - _lastReportedAt < TimeSpan.FromMilliseconds(300))
                return;
            if (!force && _files - _lastReportedFiles < 200 && now - _lastReportedAt < TimeSpan.FromMilliseconds(300))
                return;

            double? fraction = _estimatedBytes > 0
                ? Math.Min(0.99, _bytes / (double)_estimatedBytes)
                : _rootsCompleted > 0 ? Math.Min(0.99, _rootsCompleted / (double)_totalRoots) : null;
            Emit(itemsFound, fraction);
        }

        private void Emit(int itemsFound, double? fraction)
        {
            _lastReportedFiles = _files;
            _lastReportedAt = _clock.Elapsed;
            TimeSpan? eta = null;
            if (fraction is > 0.01 and < 1 && _clock.Elapsed >= TimeSpan.FromSeconds(2))
            {
                double seconds = _clock.Elapsed.TotalSeconds * (1 - fraction.Value) / fraction.Value;
                if (double.IsFinite(seconds)) eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(7).TotalSeconds));
            }
            try
            {
                _callback?.Invoke(new JunkScanProgress(_files, _bytes, itemsFound, _rootsCompleted,
                    _totalRoots, fraction, _clock.Elapsed, eta));
            }
            catch (Exception ex) { AppLog.Error("JunkScan.ProgressCallback", ex); }
        }

        private static long EstimateUsedBytes(string root)
        {
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
                if (string.IsNullOrWhiteSpace(driveRoot)) return 0;
                var drive = new DriveInfo(driveRoot);
                return drive.IsReady ? Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace) : 0;
            }
            catch { return 0; }
        }
    }

    private static object? CreateShell()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            return t is null ? null : Activator.CreateInstance(t);
        }
        catch { return null; }
    }

    private static void Add(List<JunkItem> items, JunkKind kind, string path, FileInfo f) =>
        items.Add(new JunkItem
        {
            Kind = kind,
            Path = path,
            Size = f.Length,
            Modified = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
            IsFolder = false
        });

    private static bool IsTemp(FileInfo f)
    {
        if (TempExt.Contains(f.Extension)) return true;
        if (TempNames.Contains(f.Name)) return true;
        if (f.Name.StartsWith("~$", StringComparison.Ordinal)) return true; // временные файлы Office
        if (f.Name.StartsWith("~", StringComparison.Ordinal) &&
            f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsBrokenShortcut(object shell, string lnkPath)
    {
        object? sc = null;
        try
        {
            dynamic sh = shell;
            sc = sh.CreateShortcut(lnkPath);
            string target = ((dynamic)sc!).TargetPath as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target)) return false; // не файловый ярлык
            return !File.Exists(LongPath.Fix(target)) && !Directory.Exists(LongPath.Fix(target));
        }
        catch
        {
            return false;
        }
        finally
        {
            if (sc is not null)
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); } catch { }
            }
        }
    }
}
