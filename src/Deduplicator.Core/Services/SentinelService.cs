using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>Событие сторожа: новый файл оказался копией уже проиндексированных.</summary>
/// <param name="FilePath">Полный путь нового файла.</param>
/// <param name="ExistingCount">Сколько байт-идентичных копий уже есть в индексе.</param>
/// <param name="Size">Размер файла в байтах.</param>
public sealed record SentinelDuplicateEvent(string FilePath, int ExistingCount, long Size);

public interface ISentinelService
{
    /// <summary>Идёт ли слежение сейчас (есть хотя бы один живой вотчер).</summary>
    bool IsRunning { get; }

    /// <summary>Найден новый файл-дубликат (вызывается в фоновом потоке).</summary>
    event Action<SentinelDuplicateEvent>? DuplicateFound;

    /// <summary>
    /// Начать слежение за папками. Повторный вызов безопасен: прежние вотчеры закрываются и
    /// создаются заново по новому списку (так настройки применяются «на лету» без перезапуска).
    /// </summary>
    void Start(IReadOnlyList<string> folders);

    /// <summary>Остановить слежение. Идемпотентен; не ждёт завершения текущей обработки (не виснет).</summary>
    void Stop();
}

/// <summary>
/// Сторожевой режим (Sentinel-lite — облегчённо, БЕЗ фоновой службы):
/// пока приложение запущено, FileSystemWatcher-ы следят за появлением новых файлов в выбранных
/// папках. Новые пути копятся и обрабатываются пачкой после паузы (дебаунс), затем воронка
/// как у скана, но в миниатюре: размер (нет такого размера в индексе → уникален, конец) →
/// частичный хэш → полный хэш → совпадение full_hash = дубликат → событие DuplicateFound.
/// БД при этом НЕ меняется (индекс пополняет только скан) — сторож лишь замечает и сообщает.
/// Любые ошибки глотаются в AppLog: сторож не имеет права ронять приложение.
/// </summary>
public sealed class SentinelService : ISentinelService, IDisposable
{
    /// <summary>Файлы меньше 1 КБ не интересны (как и в основном скане мелочь — шум).</summary>
    private const long MinSizeBytes = 1024;

    private readonly FileRepository _files;
    private readonly IHashService _hash;
    private readonly int _debounceMs;

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _timer;
    private CancellationTokenSource? _cts;
    private int _processing; // защита от параллельных срабатываний таймера

    public event Action<SentinelDuplicateEvent>? DuplicateFound;

    public SentinelService(FileRepository files, IHashService hash, int debounceMs = 2500)
    {
        _files = files;
        _hash = hash;
        _debounceMs = Math.Max(50, debounceMs);
        _timer = new System.Threading.Timer(_ => ProcessPending(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public bool IsRunning
    {
        get { lock (_gate) return _watchers.Count > 0; }
    }

    public void Start(IReadOnlyList<string> folders)
    {
        lock (_gate)
        {
            StopCore();
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                    var w = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        // Created/Renamed — появление нового имени. Renamed важен: загрузки
                        // (браузер, торрент) пишутся во временное имя и переименовываются в финальное.
                        NotifyFilter = NotifyFilters.FileName,
                        InternalBufferSize = 64 * 1024 // массовое копирование не должно переполнять буфер
                    };
                    w.Created += (_, e) => Enqueue(e.FullPath);
                    w.Renamed += (_, e) => Enqueue(e.FullPath);
                    w.Error += (_, e) => AppLog.Error("Sentinel.Watcher", e.GetException());
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex)
                {
                    // Папка исчезла/нет прав — пропускаем её, остальные продолжают следить.
                    AppLog.Error("Sentinel.Start", ex);
                }
            }
            if (_watchers.Count > 0)
            {
                _cts = new CancellationTokenSource();
                AppLog.Write($"Sentinel: слежение запущено, папок: {_watchers.Count}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_watchers.Count > 0) AppLog.Write("Sentinel: слежение остановлено");
            StopCore();
        }
    }

    /// <summary>Только под _gate: закрыть вотчеры, отменить обработку, сбросить очередь и таймер.</summary>
    private void StopCore()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* уже закрыт */ }
        }
        _watchers.Clear();
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _pending.Clear();
        _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    /// <summary>Событие ФС: копим пути и откладываем обработку (дебаунс — копирование пачки файлов
    /// рождает шквал событий, обрабатываем один раз после паузы).</summary>
    private void Enqueue(string path)
    {
        lock (_gate)
        {
            if (_cts is null) return; // остановлен — поздние события вотчеров игнорируем
            _pending.Add(path);
            _timer.Change(_debounceMs, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>Срабатывание таймера (фоновый поток пула): выгребаем очередь до пустоты.</summary>
    private void ProcessPending()
    {
        // Однопроходность: параллельное срабатывание выходит сразу, действующий цикл дообработает.
        if (Interlocked.Exchange(ref _processing, 1) == 1) return;
        try
        {
            while (true)
            {
                string[] batch;
                CancellationToken ct;
                lock (_gate)
                {
                    if (_cts is null || _pending.Count == 0) return;
                    batch = _pending.ToArray();
                    _pending.Clear();
                    ct = _cts.Token;
                }
                foreach (var path in batch)
                {
                    if (ct.IsCancellationRequested) return;
                    try { ProcessFile(path, ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { AppLog.Error("Sentinel.ProcessFile", ex); }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    /// <summary>Воронка для одного нового файла: размер → частичный хэш → полный хэш → событие.</summary>
    private void ProcessFile(string path, CancellationToken ct)
    {
        var info = new FileInfo(LongPath.Fix(path));
        if (!info.Exists) return; // временный файл уже исчез — не дубликат, а мусор загрузчика

        // Скрытые/системные и облачные плейсхолдеры не трогаем (чтение плейсхолдера — скачивание).
        var attrs = info.Attributes;
        if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
        if (CloudFiles.IsPlaceholder(attrs)) return;

        // Файл может ещё дописываться — ждём, пока писатель отпустит.
        if (!WaitUntilReadable(path, ct)) return;

        long size = new FileInfo(LongPath.Fix(path)).Length; // размер ПОСЛЕ стабилизации
        if (size < MinSizeBytes) return;

        // Дёшево: в индексе нет строк такого размера → файл заведомо уникален, конец.
        if (_files.CountBySize(size) == 0) return;

        var candidates = _files.GetBySize(size);
        var partial = _hash.ComputePartialHash(path, size, ct);
        var partialMatched = candidates.Where(c =>
            c.PartialHash is not null &&
            // Частичный хэш v2 для файлов > MiddleBlockThreshold отличается от v1 — сравнимы
            // только одинаковые версии; для файлов меньше порога v1 и v2 байтово совпадают.
            (size <= HashService.MiddleBlockThreshold || c.PartialVer == HashService.CurrentPartialVersion) &&
            c.PartialHash.AsSpan().SequenceEqual(partial)).ToList();
        if (partialMatched.Count == 0) return;

        var full = _hash.ComputeFullHash(path, size, ct);
        int existing = partialMatched.Count(c =>
            c.FullHash is not null &&
            c.FullHash.AsSpan().SequenceEqual(full) &&
            // Строка индекса ЭТОГО ЖЕ пути (папка сканировалась раньше) — не «уже есть копия».
            !LooksLikeSamePath(path, c));
        if (existing == 0) return;

        try { DuplicateFound?.Invoke(new SentinelDuplicateEvent(path, existing, size)); }
        catch (Exception ex) { AppLog.Error("Sentinel.DuplicateFound", ex); }
    }

    /// <summary>
    /// До 3 попыток открыть файл БЕЗ разрешения другим писать (FileShare.Read): активный писатель
    /// (докачка, копирование) даёт sharing violation — подождём и попробуем ещё раз.
    /// </summary>
    private static bool WaitUntilReadable(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException)
            {
                if (attempt < 2) ct.WaitHandle.WaitOne(500);
            }
        }
        return false;
    }

    /// <summary>Полный путь нового файла оканчивается на rel_path кандидата → это, вероятно, та же
    /// самая строка индекса (переименование/пересоздание в уже отсканированной папке), не копия.</summary>
    private static bool LooksLikeSamePath(string fullPath, FileRecord c) =>
        !string.IsNullOrEmpty(c.RelPath) &&
        fullPath.EndsWith(Path.DirectorySeparatorChar + c.RelPath, StringComparison.OrdinalIgnoreCase);
}
