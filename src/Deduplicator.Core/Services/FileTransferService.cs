using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>Элемент переноса/копирования: файл или папка (абсолютный путь источника).</summary>
public sealed record TransferItem(string SourcePath, bool IsFolder);

/// <summary>Прогресс операции: сколько файлов готово из скольких и какой файл копируется сейчас.</summary>
public sealed record TransferProgress(int Done, int Total, string CurrentName);

/// <summary>Предварительная оценка выбора: сколько файлов/папок выбрано и общий объём (папки — рекурсивно).</summary>
public sealed record TransferEstimate(int Files, int Folders, long TotalBytes, int TotalUnits);

/// <summary>Ошибка по конкретному элементу; остальные элементы операции продолжаются.</summary>
public sealed record TransferError(string SourcePath, string Message);

/// <summary>Итог операции переноса/копирования.</summary>
public sealed class TransferResult
{
    /// <summary>Скопировано выбранных элементов (файлов и папок целиком).</summary>
    public int Copied { get; set; }

    /// <summary>Перенесено выбранных элементов.</summary>
    public int Moved { get; set; }

    /// <summary>Пропущено без изменений (например, перенос в ту же самую папку).</summary>
    public int Skipped { get; set; }

    /// <summary>Всего файлов записано в назначение (включая содержимое папок).</summary>
    public int FilesTransferred { get; set; }

    /// <summary>Операция остановлена пользователем (докопировали текущий файл и остановились).</summary>
    public bool Cancelled { get; set; }

    /// <summary>Пофайловые/поэлементные ошибки — честный список того, что не получилось.</summary>
    public List<TransferError> Failed { get; } = new();
}

public interface IFileTransferService
{
    /// <summary>Подсчёт файлов/папок/объёма для диалога подтверждения (папки обходятся рекурсивно).</summary>
    TransferEstimate Estimate(IReadOnlyList<TransferItem> items);

    /// <summary>
    /// Копирует (move=false) или переносит (move=true) элементы в папку destinationDir.
    /// Существующее в назначении НИКОГДА не перезаписывается — при совпадении имени новая копия
    /// получает суффикс « (2)», « (3)»… Перенос безопасный: источник удаляется только после того,
    /// как копия записана и её содержимое проверено по хэшу (для папок — каждый файл дерева,
    /// удаление источника только после полной успешной проверки всего дерева).
    /// Ошибки по отдельным элементам не прерывают остальные. Отмена действует на границе файлов:
    /// текущий файл дописывается, дальше операция останавливается; при переносе источники всех
    /// неперенесённых элементов остаются нетронутыми.
    /// </summary>
    Task<TransferResult> TransferAsync(IReadOnlyList<TransferItem> items, string destinationDir,
        bool move, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Перенос/копирование файлов и папок между дисками для проводника.
/// Вся дисковая работа — через SafeIo (атомарная запись, проверка хэшем, длинные пути).
/// </summary>
public sealed class FileTransferService : IFileTransferService
{
    private readonly Func<string, string, bool> _sameVolume;

    public FileTransferService() : this(null) { }

    /// <summary>
    /// sameVolumeProbe — подмена определения «тот же том»: тесты передают (_, _) => false,
    /// чтобы прогнать межтомовый путь (копия → проверка → удаление) внутри одного временного тома.
    /// </summary>
    public FileTransferService(Func<string, string, bool>? sameVolumeProbe)
        => _sameVolume = sameVolumeProbe ?? SafeIo.SameVolume;

    public TransferEstimate Estimate(IReadOnlyList<TransferItem> items)
    {
        int files = 0, folders = 0, units = 0;
        long bytes = 0;
        foreach (var item in items)
        {
            if (item.IsFolder)
            {
                folders++;
                int inner = 0;
                WalkFolder(item.SourcePath, (_, size) => { inner++; bytes += size; });
                units += Math.Max(1, inner); // пустая папка — одна «единица» прогресса
            }
            else
            {
                files++;
                units++;
                try { bytes += new FileInfo(LongPath.Fix(item.SourcePath)).Length; } catch { }
            }
        }
        return new TransferEstimate(files, folders, bytes, units);
    }

    public Task<TransferResult> TransferAsync(IReadOnlyList<TransferItem> items, string destinationDir,
        bool move, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Transfer(items, destinationDir, move, progress, ct), CancellationToken.None);

    private TransferResult Transfer(IReadOnlyList<TransferItem> items, string destinationDir,
        bool move, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var result = new TransferResult();
        var destFull = Path.GetFullPath(destinationDir).TrimEnd('\\', '/');

        try
        {
            Directory.CreateDirectory(LongPath.Fix(destFull));
        }
        catch (Exception ex)
        {
            // Назначение недоступно целиком (диск выдернули) — честно фейлим все элементы.
            foreach (var item in items)
                result.Failed.Add(new TransferError(item.SourcePath, "Папка назначения недоступна: " + Friendly(ex)));
            return result;
        }

        // Пред-оценка для прогресса: сколько «единиц» (файлов) в каждом элементе.
        var perItemUnits = new int[items.Count];
        int total = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsFolder)
            {
                int inner = 0;
                WalkFolder(items[i].SourcePath, (_, _) => inner++);
                perItemUnits[i] = Math.Max(1, inner);
            }
            else perItemUnits[i] = 1;
            total += perItemUnits[i];
        }

        int baseDone = 0;
        void Report(int doneInItem, string current) =>
            progress?.Report(new TransferProgress(Math.Min(total, baseDone + doneInItem), total, current));

        for (int i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

            var item = items[i];
            var name = Path.GetFileName(item.SourcePath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = item.SourcePath;

            try
            {
                if (item.IsFolder) TransferFolder(item, name, destFull, move, result, Report, ct);
                else TransferFile(item, name, destFull, move, result, Report);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result.Failed.Add(new TransferError(item.SourcePath, Friendly(ex)));
            }
            finally
            {
                baseDone += perItemUnits[i];
            }
            Report(0, name);
        }

        return result;
    }

    private void TransferFile(TransferItem item, string name, string destFull, bool move,
        TransferResult result, Action<int, string> report)
    {
        // Перенос файла в его же папку — бессмысленная операция, честно пропускаем.
        var srcDir = Path.GetDirectoryName(Path.GetFullPath(item.SourcePath));
        if (move && PathsEqual(srcDir, destFull)) { result.Skipped++; return; }

        report(0, name);
        var dst = UniqueDestination(destFull, name, isFolder: false);
        if (move)
        {
            // На одном томе — атомарное переименование; между томами — копия → проверка → удаление.
            if (_sameVolume(item.SourcePath, dst)) SafeIo.MoveSafe(item.SourcePath, dst, CancellationToken.None);
            else SafeIo.MoveVerified(item.SourcePath, dst, CancellationToken.None);
            result.Moved++;
        }
        else
        {
            SafeIo.CopyAtomic(item.SourcePath, dst, CancellationToken.None);
            SafeIo.CopyMetadata(item.SourcePath, dst);
            result.Copied++;
        }
        result.FilesTransferred++;
        report(1, name);
    }

    private void TransferFolder(TransferItem item, string name, string destFull, bool move,
        TransferResult result, Action<int, string> report, CancellationToken ct)
    {
        var srcFull = Path.GetFullPath(item.SourcePath).TrimEnd('\\', '/');

        // Перенос папки в её же родителя — ничего делать не нужно.
        if (move && PathsEqual(Path.GetDirectoryName(srcFull), destFull)) { result.Skipped++; return; }

        // Копирование/перенос папки внутрь самой себя — бесконечная матрёшка, запрещаем сразу.
        if (IsInside(srcFull, destFull))
        {
            result.Failed.Add(new TransferError(item.SourcePath, move
                ? "Нельзя переместить папку внутрь самой себя"
                : "Нельзя скопировать папку внутрь самой себя"));
            return;
        }

        report(0, name);
        var dst = UniqueDestination(destFull, name, isFolder: true);
        int filesInItem = 0;

        void OnFileCopied(string src, string _)
        {
            filesInItem++;
            result.FilesTransferred++;
            report(filesInItem, Path.GetFileName(src));
        }

        try
        {
            if (move)
            {
                bool fullyRemoved = SafeIo.MoveTree(srcFull, dst, ct, OnFileCopied,
                    forceCrossVolume: !_sameVolume(srcFull, dst));
                result.Moved++;
                if (!fullyRemoved)
                    result.Failed.Add(new TransferError(item.SourcePath,
                        "Папка перенесена, но часть содержимого не удалось убрать из источника — проверьте папку"));
            }
            else
            {
                SafeIo.CopyTree(srcFull, dst, ct, OnFileCopied);
                result.Copied++;
            }
        }
        catch
        {
            // Сбой или отмена на этапе КОПИРОВАНИЯ: источник не тронут (удаление бывает только
            // после полной проверки), а недокопированную папку в назначении убираем — половинка
            // выглядела бы как полноценная копия. Свежую папку создали мы сами (имя уникально),
            // поэтому удалять её безопасно.
            result.FilesTransferred -= filesInItem;
            TryDeleteDir(dst);
            throw;
        }
    }

    // ---- Вспомогательные ----

    /// <summary>
    /// Свободное имя в назначении: «имя», «имя (2)», «имя (3)»… Для файлов суффикс ставится
    /// перед расширением. Существующее не перезаписывается никогда.
    /// </summary>
    internal static string UniqueDestination(string destDir, string name, bool isFolder)
    {
        for (int i = 1; ; i++)
        {
            string candidateName;
            if (i == 1) candidateName = name;
            else if (isFolder) candidateName = $"{name} ({i})";
            else
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                candidateName = $"{stem} ({i}){ext}";
            }
            var candidate = Path.Combine(destDir, candidateName);
            if (!File.Exists(LongPath.Fix(candidate)) && !Directory.Exists(LongPath.Fix(candidate)))
                return candidate;
        }
    }

    /// <summary>Рекурсивный обход папки для оценки. Ссылки и недоступные ветки пропускаются молча.</summary>
    private static void WalkFolder(string dir, Action<string, long> onFile)
    {
        try
        {
            var di = new DirectoryInfo(LongPath.Fix(dir));
            foreach (var f in di.GetFiles())
            {
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                onFile(Path.Combine(dir, f.Name), f.Length);
            }
            foreach (var sub in di.GetDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                WalkFolder(Path.Combine(dir, sub.Name), onFile);
            }
        }
        catch { /* оценка — не операция: реальный сбой покажет сам перенос */ }
    }

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>child лежит в parent или совпадает с ним.</summary>
    private static bool IsInside(string parent, string child)
    {
        var p = parent.TrimEnd('\\', '/');
        var c = child.TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(LongPath.Fix(dir)))
                Directory.Delete(LongPath.Fix(dir), recursive: true);
        }
        catch { /* лучшее усилие */ }
    }

    /// <summary>Понятное человеку описание сбоя вместо системного жаргона.</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            "Файл недоступен — возможно, диск отключён или файл был перемещён",
        UnauthorizedAccessException => "Нет доступа: файл занят другой программой или защищён",
        _ => ex.Message
    };
}
