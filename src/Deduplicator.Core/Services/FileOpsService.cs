using System.Runtime.InteropServices;
using System.Text;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IFileOpsService
{
    /// <summary>
    /// Безопасно удалить файл (в корзину или карантин) с записью в журнал и обновлением БД.
    /// Безвозвратное удаление недопустимо (FR-27).
    /// </summary>
    bool Delete(FileRecord file, string fullPath, bool useRecycleBin, long? groupId);

    /// <summary>Восстановить ранее перемещённый в карантин файл (FR-32).</summary>
    bool Restore(OperationLogEntry entry);

    /// <summary>Удалить произвольный путь (файл или папку) в Корзину Windows, с записью в журнал.</summary>
    bool RecyclePath(string fullPath);

    /// <summary>
    /// Заменить дубликат жёсткой ссылкой на эталон (NTFS, тот же том). Освобождает место без
    /// потери данных: содержимое остаётся в эталоне (FR-33, продвинутый режим).
    /// </summary>
    bool ReplaceWithHardLink(FileRecord duplicate, string duplicatePath, string representativePath, long? groupId);

    string QuarantineDir { get; }

    /// <summary>Сменить папку карантина на лету (из настроек, без перезапуска). Пустое значение
    /// игнорируется. Уже перемещённые файлы не трогаются — их путь хранится в журнале абсолютным,
    /// поэтому восстановление старых операций продолжает работать.</summary>
    void Reconfigure(string quarantineDir);
}

/// <summary>Перемещение в корзину/карантин, журнал операций и восстановление.</summary>
public sealed class FileOpsService : IFileOpsService
{
    private readonly OperationLogRepository _log;
    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly IHashService _hash;
    private readonly object _quarantineLock = new();
    private string _quarantineDir;

    /// <summary>Текущая папка карантина (может меняться из настроек — см. <see cref="Reconfigure"/>).</summary>
    public string QuarantineDir { get { lock (_quarantineLock) { return _quarantineDir; } } }

    public FileOpsService(OperationLogRepository log, FileRepository files, GroupRepository groups,
        IHashService hash, string quarantineDir)
    {
        _log = log;
        _files = files;
        _groups = groups;
        _hash = hash;
        _quarantineDir = quarantineDir;
        Directory.CreateDirectory(quarantineDir);
    }

    public void Reconfigure(string quarantineDir)
    {
        if (string.IsNullOrWhiteSpace(quarantineDir)) return;
        lock (_quarantineLock) { _quarantineDir = quarantineDir; }
        // Папку создадим сразу, чтобы «Обзор» указывал на готовое место; сбой не критичен —
        // каталог всё равно создаётся перед первым перемещением.
        try { Directory.CreateDirectory(quarantineDir); } catch { /* создастся при удалении */ }
    }

    public bool Delete(FileRecord file, string fullPath, bool useRecycleBin, long? groupId)
    {
        if (!File.Exists(LongPath.Fix(fullPath)))
            return false;

        // Корзина недоступна для этого файла (путь длиннее MAX_PATH — SHFileOperation его не примет;
        // съёмный/сетевой том — Корзины там нет, и «в Корзину» означало бы БЕЗВОЗВРАТНОЕ удаление
        // с системным диалогом на каждый файл). Фолбэк на карантин: восстановимость (FR-27) важнее
        // буквального следования настройке; журнал честно фиксирует фактическое назначение (B7).
        if (useRecycleBin && !RecycleBin.CanRecycle(fullPath))
        {
            AppLog.Write($"Корзина недоступна для «{fullPath}» (длинный путь или носитель без Корзины) — файл будет перемещён в карантин.");
            useRecycleBin = false;
        }

        long logId;

        // Запись в журнал — операция с БД: при переполненном диске/занятой БД она бросает
        // SqliteException, который раньше улетал из Delete необработанным и ронял пакетное
        // удаление целиком. Не записали намерение — файл не трогаем.
        if (useRecycleBin)
        {
            // Упреждающая запись в журнал: пишем намерение ДО перемещения. Если питание
            // пропадёт сразу после перемещения, запись о файле уже сохранена (WAL). Если
            // перемещение не удалось — запись убираем (откат). Восстановление при сбое до
            // перемещения безопасно: Restore проверяет наличие файла в назначении.
            try
            {
                logId = _log.Insert(new OperationLogEntry
                {
                    OpType = OperationType.DeleteToRecycle,
                    FileId = file.Id,
                    GroupId = groupId,
                    SrcPath = fullPath,
                    DstPath = "Корзина Windows",
                    PerformedAt = DateTimeOffset.Now,
                    Reversible = false, // восстановление — средствами Корзины Windows
                    SizeBytes = file.Size,
                    FileName = file.Name
                });
            }
            catch (Exception ex) { AppLog.Error("Delete/journal", ex); return false; }

            if (!RecycleBin.MoveToRecycleBin(fullPath))
            {
                TryDeleteLog(logId);
                return false;
            }
        }
        else
        {
            // Резервируем путь карантина, пишем журнал, затем перемещаем — всё под одним
            // замком, чтобы не было гонок при параллельных удалениях.
            lock (_quarantineLock)
            {
                var dst = BuildQuarantinePath(file);
                try
                {
                    logId = _log.Insert(new OperationLogEntry
                    {
                        OpType = OperationType.MoveToQuarantine,
                        FileId = file.Id,
                        GroupId = groupId,
                        SrcPath = fullPath,
                        DstPath = dst,
                        PerformedAt = DateTimeOffset.Now,
                        Reversible = true,
                        SizeBytes = file.Size,
                        FileName = file.Name
                    });
                }
                catch (Exception ex) { AppLog.Error("Delete/journal", ex); return false; }
                try
                {
                    SafeIo.MoveSafe(fullPath, dst);
                }
                catch
                {
                    TryDeleteLog(logId);
                    return false;
                }
            }
        }

        // Файл уже безопасно перемещён в корзину/карантин и записан в журнал.
        // Подчистку БД оборачиваем, чтобы сбой БД не прерывал пакет и не «терял» успех.
        try
        {
            if (groupId is long gid) _groups.RemoveMember(gid, file.Id);
            _files.DeleteById(file.Id);
        }
        catch (Exception ex) { AppLog.Error("Delete/db-cleanup", ex); /* запись в БД подчистится при следующем сканировании */ }
        return true;
    }

    private void TryDeleteLog(long id)
    {
        try { _log.Delete(id); }
        catch (Exception ex) { AppLog.Error("Delete/rollback-log", ex); }
    }

    public bool Restore(OperationLogEntry entry)
    {
        if (!entry.Reversible || entry.Restored) return false;
        if (entry.OpType != OperationType.MoveToQuarantine) return false;
        if (entry.DstPath is null || entry.SrcPath is null) return false;
        if (!File.Exists(LongPath.Fix(entry.DstPath))) return false;

        try
        {
            SafeIo.MoveSafe(entry.DstPath, entry.SrcPath);
        }
        catch
        {
            return false;
        }

        _log.MarkRestored(entry.Id);
        _log.Insert(new OperationLogEntry
        {
            OpType = OperationType.Restore,
            FileId = entry.FileId,
            SrcPath = entry.DstPath,
            DstPath = entry.SrcPath,
            PerformedAt = DateTimeOffset.Now,
            Reversible = false,
            Restored = true,
            SizeBytes = entry.SizeBytes,
            FileName = entry.FileName
        });
        return true;
    }

    public bool RecyclePath(string fullPath)
    {
        bool isFile = File.Exists(LongPath.Fix(fullPath));
        bool isDir = !isFile && Directory.Exists(LongPath.Fix(fullPath));
        if (!isFile && !isDir) return false;

        long? size = null;
        try { if (isFile) size = new FileInfo(LongPath.Fix(fullPath)).Length; }
        catch { }

        // Корзина недоступна (флешка/сеть/длинный путь): файлы уводим в карантин (восстановимо),
        // папки из «Мусора» — только ПУСТЫЕ, их удаляем напрямую (терять нечего) (B7).
        if (!RecycleBin.CanRecycle(fullPath))
        {
            if (isDir)
            {
                try { Directory.Delete(LongPath.Fix(fullPath), recursive: false); }
                catch { return false; } // не пустая/занята — не рискуем
                // Папка уже удалена — сбой записи в журнал (диск полон/БД занята) не должен
                // ронять пакет очистки исключением (паттерн Delete).
                try
                {
                    _log.Insert(new OperationLogEntry
                    {
                        OpType = OperationType.DeleteToRecycle,
                        SrcPath = fullPath,
                        DstPath = "Удалена пустая папка (Корзина на носителе недоступна)",
                        PerformedAt = DateTimeOffset.Now,
                        Reversible = false,
                        FileName = Path.GetFileName(fullPath.TrimEnd('\\', '/'))
                    });
                }
                catch (Exception ex) { AppLog.Error("RecyclePath/journal", ex); }
                return true;
            }

            lock (_quarantineLock)
            {
                var dst = BuildLooseQuarantinePath(fullPath);
                long qLogId;
                // Не записали намерение — файл не трогаем (иначе перемещение без следа в журнале).
                try
                {
                    qLogId = _log.Insert(new OperationLogEntry
                    {
                        OpType = OperationType.MoveToQuarantine,
                        SrcPath = fullPath,
                        DstPath = dst,
                        PerformedAt = DateTimeOffset.Now,
                        Reversible = true,
                        SizeBytes = size,
                        FileName = Path.GetFileName(fullPath)
                    });
                }
                catch (Exception ex) { AppLog.Error("RecyclePath/journal", ex); return false; }
                try { SafeIo.MoveSafe(fullPath, dst); }
                catch { TryDeleteLog(qLogId); return false; }
            }
            return true;
        }

        if (!RecycleBin.MoveToRecycleBin(fullPath)) return false;

        // Файл уже в Корзине — сбой журнала лишь логируем, пакет продолжает работать.
        try
        {
            _log.Insert(new OperationLogEntry
            {
                OpType = OperationType.DeleteToRecycle,
                SrcPath = fullPath,
                DstPath = "Корзина Windows",
                PerformedAt = DateTimeOffset.Now,
                Reversible = false,
                SizeBytes = size,
                FileName = Path.GetFileName(fullPath.TrimEnd('\\', '/'))
            });
        }
        catch (Exception ex) { AppLog.Error("RecyclePath/journal", ex); }
        return true;
    }

    public bool ReplaceWithHardLink(FileRecord dup, string dupPath, string repPath, long? groupId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (!File.Exists(LongPath.Fix(dupPath)) || !File.Exists(LongPath.Fix(repPath))) return false;
        if (string.Equals(Path.GetFullPath(dupPath), Path.GetFullPath(repPath), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!SafeIo.SameVolume(dupPath, repPath)) return false;

        // Содержимое обязано быть идентичным — иначе это не дубликат и удалять нельзя.
        try { if (!_hash.FilesAreByteEqual(dupPath, repPath)) return false; }
        catch { return false; }

        // Безопасный порядок: сначала создаём ссылку во ВРЕМЕННЫЙ путь (оригинал не трогаем),
        // и только если она создана — удаляем оригинал и атомарно ставим ссылку на его место.
        // Так оригинал не теряется, даже если CreateHardLink не сработает.
        var tmpLink = dupPath + SafeIo.HardLinkSuffix;
        try
        {
            if (File.Exists(LongPath.Fix(tmpLink))) File.Delete(LongPath.Fix(tmpLink));
            if (!CreateHardLinkW(LongPath.Fix(tmpLink), LongPath.Fix(repPath), IntPtr.Zero))
                return false; // ссылка не создана — оригинал на месте, данные целы

            // Атомарная замена оригинала ссылкой (MoveFileEx + REPLACE_EXISTING на одном томе).
            // Нет окна «оригинал удалён, а ссылка ещё не на месте» — оригинал не теряется при сбое.
            File.Move(LongPath.Fix(tmpLink), LongPath.Fix(dupPath), overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("ReplaceWithHardLink", ex);
            // Замена не удалась — оригинал не тронут. Убираем временную ссылку, если осталась.
            try { if (File.Exists(LongPath.Fix(tmpLink))) File.Delete(LongPath.Fix(tmpLink)); } catch { }
            return false;
        }

        _log.Insert(new OperationLogEntry
        {
            OpType = OperationType.Hardlink,
            FileId = dup.Id,
            GroupId = groupId,
            SrcPath = dupPath,
            DstPath = repPath,
            PerformedAt = DateTimeOffset.Now,
            Reversible = false,
            SizeBytes = dup.Size,
            FileName = dup.Name
        });

        // Файл остаётся (как жёсткая ссылка), но из группы дубликатов убираем — место уже не тратится.
        if (groupId is long gid) _groups.RemoveMember(gid, dup.Id);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Путь в карантине для «свободного» файла без записи в БД (очистка «Мусора»).</summary>
    private string BuildLooseQuarantinePath(string fullPath)
    {
        var baseDir = Path.Combine(QuarantineDir, "cleanup");
        var target = Path.Combine(baseDir, Path.GetFileName(fullPath));
        int i = 1;
        while (File.Exists(LongPath.Fix(target)))
        {
            var nameNoExt = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            target = Path.Combine(baseDir, $"{nameNoExt}__{i}{ext}");
            i++;
        }
        return target;
    }

    private string BuildQuarantinePath(FileRecord file)
    {
        // Карантин/<том>/<относительный путь> — уникальность через суффикс при коллизии.
        var rel = file.RelPath.Replace(':', '_');
        var baseDir = Path.Combine(QuarantineDir, $"vol_{file.VolumeId}");
        var target = Path.Combine(baseDir, rel);
        int i = 1;
        while (File.Exists(LongPath.Fix(target)))
        {
            var dir = Path.GetDirectoryName(target) ?? baseDir;
            var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
            var ext = Path.GetExtension(file.Name);
            target = Path.Combine(dir, $"{nameNoExt}__{i}{ext}");
            i++;
        }
        return target;
    }
}

/// <summary>Перемещение в Корзину Windows через SHFileOperation (FOF_ALLOWUNDO).</summary>
internal static class RecycleBin
{
    /// <summary>
    /// Реально ли путь попадёт в Корзину. SHFileOperation ограничен MAX_PATH и не принимает
    /// префикс \\?\; у съёмных (флешки) и сетевых томов Корзины нет вовсе — там FO_DELETE
    /// означает безвозвратное удаление с системным подтверждением на каждый файл (B7).
    /// </summary>
    public static bool CanRecycle(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (path.Length > 259 || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    public static bool MoveToRecycleBin(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Вне Windows — деградация: оставляем файл, операция считается неуспешной.
            return false;
        }

        // SHFileOperation требует список путей с ДВОЙНЫМ null-терминатором. Формируем его
        // вручную в закреплённом буфере: маршалинг LPWStr не гарантирует сохранение двойного нуля.
        byte[] buffer = Encoding.Unicode.GetBytes(path + "\0\0");
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = handle.AddrOfPinnedObject(),
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING)
            };
            int result = SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;   // указатель на буфер с двойным null-терминатором
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);
}
