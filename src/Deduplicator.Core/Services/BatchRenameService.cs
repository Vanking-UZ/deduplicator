using System.Text;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IBatchRenameService
{
    /// <summary>Построить план (предпросмотр) — без изменений на диске.</summary>
    BatchRenamePlan BuildPlan(BatchRenameRequest request);

    /// <summary>Выполнить план двухфазно с откатом при ошибке.</summary>
    BatchRenameResult Execute(BatchRenamePlan plan, CancellationToken ct,
        IProgress<TransferProgress>? progress = null);
}

/// <summary>
/// Безопасное групповое переименование (P0-B). Правила: сначала предпросмотр; запрещённые символы → «_»;
/// проверка длины пути; коллизии с существующими файлами и внутри набора; не трогаем защищённые/офлайн;
/// двухфазно (исходные → временные GUID-имена → финальные), что корректно обрабатывает свопы/циклы
/// (A↔B) и переименование с изменением только регистра; при ошибке на середине — откат всего, что можно.
/// </summary>
public sealed class BatchRenameService : IBatchRenameService
{
    private const int MaxPathLen = 259; // классический MAX_PATH-1; длиннее → конфликт (не рискуем)

    private readonly string? _journalDir;

    /// <summary>journalDir — папка write-ahead журналов (восстановление имён после сбоя питания/kill
    /// посреди операции); null — без журнала (чистые юнит-тесты).</summary>
    public BatchRenameService(string? journalDir = null) => _journalDir = journalDir;

    public BatchRenamePlan BuildPlan(BatchRenameRequest req)
    {
        string baseName = Sanitize(req.BaseName);
        int width = Math.Clamp(req.NumberWidth, 1, 9);
        var ordered = Sort(req.Files, req.SortOrder);

        var items = new List<BatchRenameItem>();
        int n = req.StartNumber;

        var newPathCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = new HashSet<string>(req.Files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var f in ordered)
        {
            string oldName = Path.GetFileName(f.FullPath);
            if (!f.CanModify)
            {
                items.Add(new BatchRenameItem(f.Id, f.FullPath, oldName, oldName, f.FullPath,
                    Skipped: true, SkipReason: "Недоступен сейчас или защищён — пропущен"));
                continue;
            }

            string dir = Path.GetDirectoryName(f.FullPath) ?? "";
            string ext = req.PreserveExtension ? Path.GetExtension(f.FullPath) : "";
            string newName = $"{baseName}_{n.ToString("D" + width)}{ext}";
            n++;
            string newFull = Path.Combine(dir, newName);

            items.Add(new BatchRenameItem(f.Id, f.FullPath, oldName, newName, newFull, Skipped: false, SkipReason: null));
            newPathCount[newFull] = newPathCount.GetValueOrDefault(newFull) + 1;
        }

        var conflicts = new List<RenameConflict>();
        foreach (var it in items)
        {
            if (it.Skipped) continue;

            if (it.NewFullPath.Length > MaxPathLen)
            {
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Слишком длинный путь"));
                continue;
            }
            if (newPathCount[it.NewFullPath] > 1)
            {
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Два файла получают одинаковое имя"));
                continue;
            }
            // Существующий файл-помеха ВНЕ переименовываемого набора (файлы из набора уедут — это нормально).
            if (File.Exists(LongPath.Fix(it.NewFullPath)) && !sourcePaths.Contains(it.NewFullPath))
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Файл с таким именем уже существует"));
        }

        bool canExecute = conflicts.Count == 0 && items.Any(i => !i.Skipped);
        return new BatchRenamePlan(items, conflicts, canExecute);
    }

    public BatchRenameResult Execute(BatchRenamePlan plan, CancellationToken ct,
        IProgress<TransferProgress>? progress = null)
    {
        int skipped = plan.Items.Count(i => i.Skipped);
        if (!plan.CanExecute)
            return new BatchRenameResult(0, skipped, 0, RolledBack: false,
                Error: "План содержит конфликты или нечего переименовывать.",
                RenamedPairs: Array.Empty<RenamePair>(), Orphans: Array.Empty<OrphanedRename>());

        var todo = plan.Items.Where(i => !i.Skipped).ToList();

        // Временные имена генерируем ЗАРАНЕЕ и фиксируем весь план write-ahead журналом на диске
        // ДО первого переименования (NFR-2): kill/потеря питания посреди операции больше не
        // оставляет файлы безымянными — RenameJournal.RecoverAll при старте вернёт исходные имена.
        var temps = new List<string>(todo.Count);
        foreach (var it in todo)
        {
            string dir = Path.GetDirectoryName(it.OldFullPath) ?? "";
            temps.Add(Path.Combine(dir, ".dedup-ren-" + Guid.NewGuid().ToString("N") + ".tmp"));
        }
        var journal = RenameJournal.TryBegin(_journalDir,
            todo.Select((it, i) => new RenameJournal.Entry(it.OldFullPath, temps[i], it.NewFullPath)).ToList());

        // moved[i] = (текущее расположение файла, куда вернуть при откате). Изначально текущее = исходное.
        var moved = new List<(string current, string original)>();

        try
        {
            // Фаза 1: исходные → временные имена (разрывает свопы/циклы и case-only).
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(LongPath.Fix(todo[i].OldFullPath), LongPath.Fix(temps[i]));
                moved.Add((temps[i], todo[i].OldFullPath));
                progress?.Report(new TransferProgress(i + 1, todo.Count * 2,
                    "Подготовка: " + Path.GetFileName(todo[i].OldFullPath)));
            }

            // Фаза 2: временные → финальные.
            var done = new List<RenamePair>(todo.Count);
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = todo[i];
                File.Move(LongPath.Fix(temps[i]), LongPath.Fix(it.NewFullPath));
                moved[i] = (it.NewFullPath, it.OldFullPath); // теперь файл на финальном месте
                done.Add(new RenamePair(it.FileId, it.OldFullPath, it.NewFullPath));
                progress?.Report(new TransferProgress(todo.Count + i + 1, todo.Count * 2,
                    Path.GetFileName(it.NewFullPath)));
            }

            RenameJournal.TryComplete(journal);
            return new BatchRenameResult(done.Count, skipped, 0, RolledBack: false, Error: null,
                RenamedPairs: done, Orphans: Array.Empty<OrphanedRename>());
        }
        catch (Exception ex)
        {
            AppLog.Error("BatchRename.Execute", ex);
            // Откат двухпроходный. Прямой откат «в обратном порядке» ломался на цепочках имён
            // (новое имя одного файла = старое имя другого): первый же возврат упирался в занятое
            // имя, и полностью восстановимый файл становился «сиротой». Поэтому: (1) все файлы,
            // уже вставшие на ФИНАЛЬНЫЕ места, уводим обратно на их временные GUID-имена — они
            // гарантированно свободны; (2) затем все временные возвращаем на исходные — исходные
            // слоты к этому моменту освобождены фазой 1.
            // СРАВНЕНИЕ Ordinal (с учётом регистра): иначе rename, меняющий только регистр (foo→FOO),
            // считался бы «ничем не двигался» и НЕ откатывался. temp-имена — GUID, совпадений нет.
            var orphans = new List<OrphanedRename>();
            for (int i = 0; i < moved.Count; i++)
            {
                var (current, original) = moved[i];
                if (string.Equals(current, temps[i], StringComparison.Ordinal) ||
                    string.Equals(current, original, StringComparison.Ordinal)) continue;
                try { File.Move(LongPath.Fix(current), LongPath.Fix(temps[i])); moved[i] = (temps[i], original); }
                catch { /* второй проход попробует вернуть напрямую */ }
            }
            for (int i = 0; i < moved.Count; i++)
            {
                var (current, original) = moved[i];
                if (string.Equals(current, original, StringComparison.Ordinal)) continue; // двигать нечего
                try { File.Move(LongPath.Fix(current), LongPath.Fix(original)); }
                catch { orphans.Add(new OrphanedRename(current, original)); } // файл не потерян — путь и исходное имя известны
            }

            // Полный откат → журнал не нужен. Остались сироты → журнал СОХРАНЯЕМ:
            // следующий старт приложения попробует вернуть имена ещё раз.
            if (orphans.Count == 0) RenameJournal.TryComplete(journal);

            // Файл/папка исчезли посреди операции (удалили или выдернули диск) — системное
            // сообщение об этом малопонятно; говорим простыми словами.
            string error = ex switch
            {
                OperationCanceledException => "Операция остановлена. Выполненные переименования отменены.",
                FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException =>
                    "Файл недоступен — возможно, диск отключён.",
                _ => "Переименование не завершено. Исходные имена восстановлены; подробности сохранены в журнале."
            };

            return new BatchRenameResult(0, skipped, todo.Count, RolledBack: true, Error: error,
                RenamedPairs: Array.Empty<RenamePair>(), Orphans: orphans);
        }
    }

    private static IReadOnlyList<RenameSourceFile> Sort(IReadOnlyList<RenameSourceFile> files, RenameSortOrder order) => order switch
    {
        RenameSortOrder.CreatedDate => files.OrderBy(f => f.Created).ToList(),
        RenameSortOrder.ModifiedDate => files.OrderBy(f => f.Modified).ToList(),
        RenameSortOrder.Name => files.OrderBy(f => Path.GetFileName(f.FullPath), StringComparer.OrdinalIgnoreCase).ToList(),
        RenameSortOrder.Size => files.OrderBy(f => f.Size).ToList(),
        _ => files
    };

    /// <summary>Заменить запрещённые в Windows символы на «_», убрать хвостовые точки/пробелы.</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "file" : s;
    }
}
