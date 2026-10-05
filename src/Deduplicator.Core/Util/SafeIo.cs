using System.IO.Hashing;
using System.Text;

namespace Deduplicator.Core.Util;

/// <summary>
/// Безопасные файловые операции, устойчивые к сбоям/потере питания (NFR-2):
/// запись во временный файл с принудительным сбросом на диск и атомарным переименованием;
/// межтомовое перемещение — копирование + проверка содержимого по хэшу + удаление источника.
/// Источник никогда не удаляется, пока копия не подтверждена.
/// </summary>
public static class SafeIo
{
    public const string TempSuffix = ".dedup-tmp";
    public const string HardLinkSuffix = ".dedup-hl";
    private const int Buffer = 1 << 20;

    /// <summary>Прочитать небольшой служебный текст с жёстким пределом размера.</summary>
    public static string ReadAllTextLimited(string path, long maxBytes)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var stream = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            throw new InvalidDataException($"Служебный файл слишком большой ({stream.Length:N0} байт).");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024, leaveOpen: false);
        var text = reader.ReadToEnd();
        // Для UTF-8 число символов не превышает число байт; дополнительная проверка защищает и
        // нестандартные BOM-кодировки, если файл заменили сразу после открытия.
        if (text.Length > maxBytes)
            throw new InvalidDataException("Служебный файл превышает допустимый размер.");
        return text;
    }

    /// <summary>Атомарно записать служебный UTF-8 текст рядом с целевым файлом.</summary>
    public static void WriteAllTextAtomic(string path, string content, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));
        string temp = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            using (var stream = new FileStream(LongPath.Fix(temp), FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, encoding, 16 * 1024, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(LongPath.Fix(temp), LongPath.Fix(path), overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Атомарно копирует src → dst: пишет во временный файл, сбрасывает на диск, затем
    /// атомарно переименовывает. При сбое остаётся либо старый файл, либо ничего (но не «половина»).
    /// </summary>
    public static void CopyAtomic(string src, string dst, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));

        var tmp = dst + TempSuffix;
        try
        {
            using (var input = new FileStream(LongPath.Fix(src), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan))
            using (var output = new FileStream(LongPath.Fix(tmp), FileMode.Create, FileAccess.Write,
                       FileShare.None, Buffer, FileOptions.WriteThrough))
            {
                CopyStream(input, output, ct);
                output.Flush(flushToDisk: true);
            }

            // Публикация результата атомарным переименованием.
            File.Move(LongPath.Fix(tmp), LongPath.Fix(dst), overwrite: false);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Безопасно переносит src → dst. В пределах одного тома — атомарное переименование;
    /// между томами — копирование с проверкой и последующим удалением источника.
    /// </summary>
    public static void MoveSafe(string src, string dst, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));

        if (SameVolume(src, dst))
        {
            File.Move(LongPath.Fix(src), LongPath.Fix(dst), overwrite: false);
            return;
        }

        MoveVerified(src, dst, ct);
    }

    /// <summary>
    /// Межтомовый перенос файла: копируем, проверяем содержимое по хэшу, только потом удаляем
    /// источник. Так даже «тихое» повреждение (тот же размер, другие байты) не приведёт к потере.
    /// </summary>
    public static void MoveVerified(string src, string dst, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));

        CopyVerified(src, dst, ct);
        try
        {
            DeleteAllowReadOnly(src);
        }
        catch
        {
            // Источник удалить не удалось — подчищаем копию, иначе в назначении (карантине)
            // остаётся неучтённая копия-сирота, множащаяся при повторных попытках.
            TryDelete(dst);
            throw;
        }
    }

    /// <summary>
    /// Копирует src → dst с проверкой содержимого: хэш источника сверяется с хэшем готовой копии.
    /// Источник не изменяется. При расхождении копия удаляется и бросается IOException.
    /// Даты и атрибуты файла переносятся на копию.
    /// </summary>
    public static void CopyVerified(string src, string dst, CancellationToken ct = default)
    {
        var srcHash = HashFile(src, ct);
        CopyAtomic(src, dst, ct);
        var dstHash = HashFile(dst, ct);
        if (!srcHash.AsSpan().SequenceEqual(dstHash))
        {
            TryDelete(dst);
            throw new IOException($"Копия повреждена (хэш не совпал): {src}");
        }
        CopyMetadata(src, dst);
    }

    /// <summary>
    /// Переносит на копию даты создания/изменения и атрибуты источника (лучшее усилие:
    /// сбой метаданных не должен ронять операцию — содержимое уже скопировано и проверено).
    /// </summary>
    public static void CopyMetadata(string src, string dst)
    {
        try
        {
            var fi = new FileInfo(LongPath.Fix(src));
            var target = LongPath.Fix(dst);
            File.SetCreationTimeUtc(target, fi.CreationTimeUtc);
            File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
            File.SetAttributes(target, fi.Attributes);
        }
        catch { /* метаданные — не повод терять уже проверенную копию */ }
    }

    /// <summary>
    /// Удаление файла, санкционированное пользователем: атрибут «только для чтения» не повод
    /// падать (файлы с CD/бэкапов). File.Move на одном томе переносит read-only без вопросов,
    /// а File.Delete в межтомовой ветке бросал UnauthorizedAccessException — и это ломало
    /// удаление в карантин для целого класса файлов на внешних носителях.
    /// </summary>
    private static void DeleteAllowReadOnly(string path)
    {
        var p = LongPath.Fix(path);
        try
        {
            File.Delete(p);
        }
        catch (UnauthorizedAccessException)
        {
            var attrs = File.GetAttributes(p);
            if ((attrs & FileAttributes.ReadOnly) == 0) throw; // не read-only — настоящая проблема прав
            File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
            File.Delete(p);
        }
    }

    private static byte[] HashFile(string path, CancellationToken ct)
    {
        using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan);
        var hasher = new XxHash128();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Buffer);
        try
        {
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, n));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return hasher.GetCurrentHash();
    }

    /// <summary>
    /// Рекурсивно копирует дерево каталогов (каждый файл — атомарно, с переносом дат/атрибутов).
    /// Отмена действует на границах файлов: начатый файл дописывается до конца.
    /// <paramref name="onFileCopied"/> вызывается после каждого файла (источник, копия) — для прогресса.
    /// </summary>
    public static void CopyTree(string srcDir, string dstDir, CancellationToken ct = default,
        Action<string, string>? onFileCopied = null)
        => CopyTreeCore(srcDir, dstDir, ct, verify: false, onFileCopied, copiedSources: null);

    /// <summary>
    /// Безопасно переносит дерево каталогов. В пределах одного тома — атомарный Directory.Move;
    /// между томами — копия каждого файла с проверкой содержимого по хэшу (как MoveSafe), и только
    /// после того как ВСЁ дерево скопировано и проверено, источник удаляется файл за файлом.
    /// Возвращает true, если исходная папка удалена полностью; false — данные целы и перенесены,
    /// но в источнике остались пропущенные элементы (ссылки/занятые файлы) — папка оставлена.
    /// <paramref name="onFileCopied"/> — прогресс по файлам; <paramref name="forceCrossVolume"/> —
    /// принудительно межтомовый путь (для тестов и особых случаев).
    /// Отмена действует на границах файлов; до удаления источника дело при отмене не доходит.
    /// </summary>
    public static bool MoveTree(string srcDir, string dstDir, CancellationToken ct = default,
        Action<string, string>? onFileCopied = null, bool forceCrossVolume = false)
    {
        // Защита: нельзя переносить папку в саму себя или в свой подкаталог.
        var srcFull = Path.GetFullPath(srcDir).TrimEnd('\\', '/');
        var dstFull = Path.GetFullPath(dstDir).TrimEnd('\\', '/');
        if (dstFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase) ||
            dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Нельзя переместить папку внутрь самой себя");

        if (!forceCrossVolume && SameVolume(srcDir, dstDir))
        {
            Directory.CreateDirectory(LongPath.Fix(Path.GetDirectoryName(dstDir) ?? dstDir));
            Directory.Move(LongPath.Fix(srcDir), LongPath.Fix(dstDir));
            return true;
        }

        // Межтомовой перенос. Этап 1: копируем всё дерево, каждый файл проверяется по хэшу.
        // Любой сбой здесь прерывает операцию, источник ещё не тронут.
        var copiedSources = new List<string>();
        CopyTreeCore(srcDir, dstDir, ct, verify: true, onFileCopied, copiedSources);

        // Этап 2: всё скопировано и проверено — теперь (и только теперь) удаляем источник.
        // Удаление — лучшее усилие: занятый файл не должен ронять операцию (копия-то уже есть),
        // он просто останется в источнике, и мы честно вернём false.
        bool allDeleted = true;
        foreach (var src in copiedSources)
        {
            try { DeleteAllowReadOnly(src); }
            catch { allDeleted = false; }
        }
        return allDeleted && PruneEmptyDirs(srcFull);
    }

    /// <summary>
    /// Общая рекурсия копирования дерева. Ссылки (reparse points) пропускаются и у папок, и у
    /// файлов — иначе перенос удалил бы «файл-цель» через ссылку. verify — сверять хэш каждой копии
    /// (режим переноса); copiedSources — накопитель успешно скопированных источников.
    /// </summary>
    private static void CopyTreeCore(string srcDir, string dstDir, CancellationToken ct, bool verify,
        Action<string, string>? onFileCopied, List<string>? copiedSources)
    {
        Directory.CreateDirectory(LongPath.Fix(dstDir));
        var di = new DirectoryInfo(LongPath.Fix(srcDir));
        foreach (var file in di.GetFiles())
        {
            // Отмена — на границе файлов: начатый файл всегда дописывается целиком.
            ct.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var src = Path.Combine(srcDir, file.Name);
            var dst = Path.Combine(dstDir, file.Name);
            if (verify)
            {
                CopyVerified(src, dst, CancellationToken.None);
            }
            else
            {
                CopyAtomic(src, dst, CancellationToken.None);
                CopyMetadata(src, dst);
            }
            copiedSources?.Add(src);
            onFileCopied?.Invoke(src, dst);
        }
        foreach (var sub in di.GetDirectories())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyTreeCore(Path.Combine(srcDir, sub.Name), Path.Combine(dstDir, sub.Name),
                ct, verify, onFileCopied, copiedSources);
        }
    }

    /// <summary>
    /// Удаляет ставшие пустыми каталоги снизу вверх (не рекурсивным Delete — папка исчезает,
    /// только если она действительно пуста). true — корень удалён полностью.
    /// </summary>
    private static bool PruneEmptyDirs(string root)
    {
        try
        {
            var di = new DirectoryInfo(LongPath.Fix(root));
            foreach (var sub in di.GetDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                PruneEmptyDirs(Path.Combine(root, sub.Name));
            }
            Directory.Delete(LongPath.Fix(root), recursive: false);
            return true;
        }
        catch { return false; } // внутри что-то осталось (ссылки/недоступное) — данные целы
    }

    public static bool SameVolume(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(b));
        return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Удалить «осиротевшие» временные файлы (вызывать при старте — восстановление после сбоя).</summary>
    public static int CleanupTempFiles(string root)
    {
        int removed = 0;
        foreach (var suffix in new[] { TempSuffix, HardLinkSuffix })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(LongPath.Fix(root), "*" + suffix,
                             SearchOption.AllDirectories))
                {
                    if (TryDelete(f)) removed++;
                }
            }
            catch { /* лучшее усилие */ }
        }
        return removed;
    }

    private static void CopyStream(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Buffer);
        try
        {
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(LongPath.Fix(path))) { File.Delete(LongPath.Fix(path)); return true; }
        }
        catch { }
        return false;
    }
}
