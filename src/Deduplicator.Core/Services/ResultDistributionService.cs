using System.Globalization;
using System.Text;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IResultDistributionService
{
    /// <summary>Построить план раскладки (предпросмотр) — без изменений на диске.</summary>
    DistributionPlan BuildPlan(DistributionRequest request);

    /// <summary>Выполнить: ReportOnly пишет CSV; CopyFiles безопасно копирует (оригиналы не трогаются).</summary>
    DistributionResult Execute(DistributionPlan plan, DistributionRequest request, CancellationToken ct,
        IProgress<TransferProgress>? progress = null);
}

/// <summary>
/// Безопасное распределение результатов по папкам (P0-D). НИКОГДА не перемещает и не удаляет оригиналы:
/// только отчёт или копирование с проверкой после копирования (copy→temp→verify→rename). Раскладка по
/// «категория /band похожести». Имя-коллизия разрешается добавлением номера (_2, _3…), а не перезаписью.
/// </summary>
public sealed class ResultDistributionService : IResultDistributionService
{
    public DistributionPlan BuildPlan(DistributionRequest req)
    {
        var items = new List<DistributionItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in req.Files)
        {
            string folderKey = Path.Combine(SanitizeSeg(f.Category), SanitizeSeg(Band(f.Similarity)));
            string targetDir = Path.Combine(req.OutputRoot, folderKey);
            string name = Path.GetFileName(f.FullPath);

            string target = Path.Combine(targetDir, name);
            // Имя-коллизия (в плане ИЛИ уже на диске) → добавляем _2, _3… (никогда не перезаписываем).
            if (!used.Add(target) || File.Exists(LongPath.Fix(target)))
            {
                string baseName = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                int k = 2;
                do { target = Path.Combine(targetDir, $"{baseName}_{k++}{ext}"); }
                while (!used.Add(target) || File.Exists(LongPath.Fix(target)));
            }

            items.Add(new DistributionItem(f.Id, f.FullPath, target, folderKey, Skipped: false, Reason: null));
        }

        return new DistributionPlan(items, items.Count > 0);
    }

    public DistributionResult Execute(DistributionPlan plan, DistributionRequest req, CancellationToken ct,
        IProgress<TransferProgress>? progress = null)
    {
        if (!plan.CanExecute)
            return new DistributionResult(0, 0, 0, null, "Пустой план.");

        try
        {
            Directory.CreateDirectory(req.OutputRoot);

            if (req.Mode == DistributionMode.ReportOnly)
            {
                string report = WriteReport(plan, req.OutputRoot, ct, progress);
                return new DistributionResult(0, 0, 0, report, null);
            }

            int copied = 0, failed = 0, unavailable = 0;
            int done = 0;
            int total = plan.Items.Count(it => !it.Skipped);
            foreach (var it in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (it.Skipped) continue;
                try
                {
                    if (CopyVerified(it.SourcePath, it.TargetPath, req.VerifyAfterCopy, ct)) copied++;
                    else failed++;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Сбой одного файла не валит пакет. Источник исчез (файл удалили или диск
                    // выдернули) — считаем отдельно: это не «ошибка копирования», а честное
                    // «недоступен», о чём UI сообщает своими словами.
                    if (!File.Exists(LongPath.Fix(it.SourcePath))) unavailable++;
                    else failed++;
                }
                finally
                {
                    done++;
                    progress?.Report(new TransferProgress(done, total, Path.GetFileName(it.SourcePath)));
                }
            }

            // Отчёт пишем ПОСЛЕ копирования, и его сбой (папка назначения исчезла/защищена)
            // не должен «съедать» счётчики уже сделанной работы (выдернутый диск).
            string? rep = null;
            string? error = null;
            try { rep = WriteReport(plan, req.OutputRoot, ct, progress: null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error("ResultDistribution.WriteReport", ex);
                error = "отчёт не записан — проверьте доступ к папке и свободное место";
            }
            return new DistributionResult(copied, 0, failed, rep, error, unavailable);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("ResultDistribution.Execute", ex);
            return new DistributionResult(0, 0, 0, null,
                "не удалось подготовить папку назначения — проверьте доступ и свободное место");
        }
    }

    /// <summary>Копирование через временный файл + проверка (размер, при VerifyAfterCopy — побайтно). Источник НЕ удаляем.</summary>
    private static bool CopyVerified(string source, string target, bool verify, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(target) ?? "";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, ".dedup-dist-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using var input = new FileStream(LongPath.Fix(source), FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(LongPath.Fix(temp), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
            output.Flush(flushToDisk: true);
        }
        catch
        {
            try { File.Delete(LongPath.Fix(temp)); } catch { }
            throw;
        }

        try
        {
            var srcLen = new FileInfo(LongPath.Fix(source)).Length;
            var tmpLen = new FileInfo(LongPath.Fix(temp)).Length;
            if (srcLen != tmpLen || (verify && !SameContent(source, temp, ct)))
                return false;

            // Финализируем (если цель уже появилась — не перезаписываем, считаем неуспехом).
            if (File.Exists(LongPath.Fix(target))) return false;
            try
            {
                File.Move(LongPath.Fix(temp), LongPath.Fix(target));
                return true;
            }
            catch
            {
                return false;
            }
        }
        finally
        {
            // Отмена, несовпадение, гонка или блокировка на финальном Move не оставляют .tmp-файл.
            try { File.Delete(LongPath.Fix(temp)); } catch { }
        }
    }

    private static bool SameContent(string a, string b, CancellationToken ct)
    {
        using var fa = File.OpenRead(LongPath.Fix(a));
        using var fb = File.OpenRead(LongPath.Fix(b));
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        int ra;
        while ((ra = fa.Read(ba, 0, ba.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int off = 0;
            while (off < ra)
            {
                int rb = fb.Read(bb, off, ra - off);
                if (rb == 0) return false;
                off += rb;
            }
            for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
        }
        return fb.ReadByte() == -1; // у b не должно остаться лишних байт
    }

    private static string WriteReport(DistributionPlan plan, string outputRoot, CancellationToken ct,
        IProgress<TransferProgress>? progress)
    {
        string path = Path.Combine(outputRoot, "distribution-report.csv");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(true), 64 * 1024))
            {
                writer.WriteLine("Папка;Источник;Цель");
                int done = 0;
                foreach (var it in plan.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(Csv(it.FolderKey));
                    writer.Write(';');
                    writer.Write(Csv(it.SourcePath));
                    writer.Write(';');
                    writer.WriteLine(Csv(it.TargetPath));
                    progress?.Report(new TransferProgress(++done, plan.Items.Count,
                        Path.GetFileName(it.SourcePath)));
                }
            }
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Band похожести для папки-раскладки (60+: понятные подписи).</summary>
    public static string Band(double similarity) => similarity switch
    {
        >= 0.95 => "Очень похожие",
        >= 0.85 => "Похожие",
        _ => "Проверить вручную"
    };

    private static string SanitizeSeg(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Прочее";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.', ' ');
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
