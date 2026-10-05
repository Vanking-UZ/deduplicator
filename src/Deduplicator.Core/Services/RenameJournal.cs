using System.Text.Json;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>
/// Write-ahead журнал группового переименования (NFR-2). Полный план (исходное → временное →
/// финальное имя) записывается на диск ДО первого переименования; при штатном завершении файл
/// журнала удаляется. Если процесс убит или пропало питание посреди операции, файлы остаются под
/// временными «.dedup-ren-*.tmp» именами — <see cref="RecoverAll"/> при старте приложения
/// возвращает им исходные имена по журналу (двухпроходно, устойчиво к цепочкам/свопам имён).
/// </summary>
public static class RenameJournal
{
    /// <summary>Одна строка плана: исходный путь, временное GUID-имя, финальный путь.</summary>
    public sealed record Entry(string Original, string Temp, string Final);

    private const string Prefix = "rename-journal-";
    private const int MaxJournalBytes = 128 * 1024 * 1024;

    /// <summary>Зафиксировать план на диске. null-dir (тесты) или сбой записи → без журнала.</summary>
    public static string? TryBegin(string? dir, IReadOnlyList<Entry> entries)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Prefix + Guid.NewGuid().ToString("N") + ".json");
            SafeIo.WriteAllTextAtomic(path, JsonSerializer.Serialize(entries,
                new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Error("RenameJournal.Begin", ex);
            return null; // журнал — дополнительная защита; операцию из-за него не отменяем
        }
    }

    /// <summary>Операция завершена (успех или полный откат) — журнал больше не нужен.</summary>
    public static void TryComplete(string? journalPath)
    {
        if (journalPath is null) return;
        try { File.Delete(journalPath); }
        catch (Exception ex) { AppLog.Error("RenameJournal.Complete", ex); }
    }

    /// <summary>
    /// Восстановление при старте: для каждого незавершённого журнала вернуть файлы к исходным
    /// именам. Проход 1 — файлы, уже вставшие на финальные места, уводятся обратно на свои
    /// временные имена (освобождает имена, занятые в цепочках «новое одного = старое другого»);
    /// проход 2 — все временные возвращаются на исходные. Журнал удаляется только при полном успехе.
    /// </summary>
    public static (int Plans, int Restored, List<string> Problems) RecoverAll(string dir)
    {
        int plans = 0, restored = 0;
        var problems = new List<string>();
        List<string> journals;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0, problems);
            journals = Directory.EnumerateFiles(dir, Prefix + "*.json").ToList();
        }
        catch { return (0, 0, problems); }

        foreach (var journal in journals)
        {
            plans++;
            List<Entry>? entries;
            try { entries = JsonSerializer.Deserialize<List<Entry>>(
                SafeIo.ReadAllTextLimited(journal, MaxJournalBytes)); }
            catch (Exception ex) { problems.Add($"{journal}: журнал не читается ({ex.Message})"); continue; }
            if (entries is null || entries.Count == 0) { TryComplete(journal); continue; }

            bool clean = true;

            // Проход 1: финальное → временное. Temp-имена уникальны (GUID) и свободны.
            // «Original существует» = файл никуда не уходил (или case-only) — не трогаем.
            foreach (var e in entries)
            {
                try
                {
                    if (File.Exists(LongPath.Fix(e.Original)) || File.Exists(LongPath.Fix(e.Temp))) continue;
                    if (File.Exists(LongPath.Fix(e.Final)))
                        File.Move(LongPath.Fix(e.Final), LongPath.Fix(e.Temp));
                }
                catch (Exception ex) { clean = false; problems.Add($"{e.Final}: {ex.Message}"); }
            }

            // Проход 2: временное → исходное.
            foreach (var e in entries)
            {
                try
                {
                    if (!File.Exists(LongPath.Fix(e.Temp))) continue;
                    if (File.Exists(LongPath.Fix(e.Original)))
                    {
                        clean = false;
                        problems.Add($"{e.Temp}: исходное имя занято ({e.Original})");
                        continue;
                    }
                    File.Move(LongPath.Fix(e.Temp), LongPath.Fix(e.Original));
                    restored++;
                }
                catch (Exception ex) { clean = false; problems.Add($"{e.Temp}: {ex.Message}"); }
            }

            if (clean) TryComplete(journal);
            else AppLog.Write($"Восстановление переименования по «{journal}» — с проблемами, журнал сохранён для следующей попытки.");
        }

        if (restored > 0 || problems.Count > 0)
            AppLog.Write($"Восстановление переименований: планов {plans}, возвращено файлов {restored}, проблем {problems.Count}.");
        return (plans, restored, problems);
    }
}
