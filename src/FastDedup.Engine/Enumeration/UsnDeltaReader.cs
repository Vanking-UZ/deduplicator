using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

/// <summary>Одна запись дельты USN-журнала (сырой вид, путь ещё не восстановлен).</summary>
public readonly record struct UsnDeltaRecord(
    ulong Frn, ulong ParentFrn, string FileName, uint Attributes, uint Reason, long Usn);

/// <summary>Изменение из дельты журнала с уже восстановленным полным путём.</summary>
public readonly record struct UsnDeltaChange(string FullPath, uint Reason, bool IsDirectory);

/// <summary>
/// Результат чтения дельты: либо список изменений, либо честное «дельта недоступна» с причиной
/// (журнал усечён/удаляется, слишком большая дельта, запись неподдерживаемой версии, путь не
/// восстановился). Недоступность — НЕ исключение и НЕ ошибка: вызывающий делает полный пересбор
/// тома, данные при этом не теряются.
/// </summary>
public sealed class UsnDeltaResult
{
    public bool Available { get; }
    public string? UnavailableReason { get; }
    public IReadOnlyList<UsnDeltaChange> Changes { get; }

    private UsnDeltaResult(bool available, string? reason, IReadOnlyList<UsnDeltaChange> changes)
    {
        Available = available;
        UnavailableReason = reason;
        Changes = changes;
    }

    public static UsnDeltaResult Ok(IReadOnlyList<UsnDeltaChange> changes) => new(true, null, changes);
    public static UsnDeltaResult Unavailable(string reason) => new(false, reason, Array.Empty<UsnDeltaChange>());
}

/// <summary>
/// Чтение ДЕЛЬТЫ USN-журнала (FSCTL_READ_USN_JOURNAL, READ_USN_JOURNAL_DATA_V0) от сохранённого
/// водяного знака до снимка на старте текущего скана + восстановление полных путей по FRN
/// родителей (OpenFileById → GetFinalPathNameByHandle). Карта всей MFT НЕ строится — записей в
/// дельте мало (кап <see cref="MaxRecords"/>), родителей ещё меньше, каждый открывается один раз.
///
/// Принцип «дельта не теряет файлы»: ЛЮБАЯ аномалия (усечение журнала, версия записи ≠ V2, запись
/// без имени, невосстановимый путь, превышен кап, любой сбой ОС) даёт Unavailable с причиной, а не
/// частичный список — вызывающий обязан сделать полный пересбор тома.
///
/// ВНИМАНИЕ: сам FSCTL-путь требует прав администратора и NTFS и НЕ проверен в тестовом окружении
/// (тесты идут без прав → журнал недоступен). Разбор буфера записей (<see cref="TryAppendRecords"/>)
/// чистый и покрыт тестами на буферах, собранных руками; классификация и применение дельты — в
/// Deduplicator.Core (UsnDeltaPlanner/UsnDeltaExecutor), тоже под тестами.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsnDeltaReader
{
    /// <summary>Кап записей дельты: больше — полный пересбор дешевле и надёжнее точечного реконсайла.</summary>
    public const int MaxRecords = 100_000;

    /// <summary>
    /// Прочитать дельту журнала [startUsn, stopUsn) и восстановить полные пути записей.
    /// stopUsn — NextUsn из снимка журнала на старте скана: ровно до него реконсайл гарантирован,
    /// он же станет новым водяным знаком; изменения выше дочитает следующий скан (идемпотентно).
    /// </summary>
    public static UsnDeltaResult ReadResolved(string volumeRoot, ulong journalId, long startUsn, long stopUsn)
    {
        try
        {
            return ReadCore(volumeRoot, journalId, startUsn, stopUsn);
        }
        catch (Exception ex)
        {
            // Никогда не роняем скан из-за дельты — честная причина и полный пересбор.
            return UsnDeltaResult.Unavailable("сбой чтения журнала: " + ex.Message);
        }
    }

    private static UsnDeltaResult ReadCore(string volumeRoot, ulong journalId, long startUsn, long stopUsn)
    {
        if (startUsn < 0 || stopUsn < startUsn)
            return UsnDeltaResult.Unavailable($"несогласованный диапазон USN [{startUsn}; {stopUsn})");

        string drive = UsnJournalReader.VolumeOf(volumeRoot);   // "C:"
        using var h = Native.OpenVolumeRead(@"\\.\" + drive);   // нужны права администратора
        if (h.IsInvalid)
            return UsnDeltaResult.Unavailable("том недоступен для чтения журнала");

        var records = new List<UsnDeltaRecord>();

        const int InSize = 40;       // READ_USN_JOURNAL_DATA_V0
        const int OutSize = 1 << 20; // 1 МБ за вызов
        IntPtr inBuf = Marshal.AllocHGlobal(InSize);
        IntPtr outBuf = Marshal.AllocHGlobal(OutSize);
        var managed = new byte[OutSize];
        try
        {
            long cursor = startUsn;
            while (cursor < stopUsn)
            {
                // READ_USN_JOURNAL_DATA_V0: StartUsn(8) ReasonMask(4) ReturnOnlyOnClose(4)
                //                           Timeout(8) BytesToWaitFor(8) UsnJournalID(8).
                Marshal.WriteInt64(inBuf, 0, cursor);
                Marshal.WriteInt32(inBuf, 8, unchecked((int)0xFFFFFFFF)); // все причины
                Marshal.WriteInt32(inBuf, 12, 0);                          // не только on-close
                Marshal.WriteInt64(inBuf, 16, 0L);                         // Timeout = 0
                Marshal.WriteInt64(inBuf, 24, 0L);                         // не ждать новых записей
                Marshal.WriteInt64(inBuf, 32, unchecked((long)journalId));

                bool ok = Native.DeviceIoControl(
                    h, Native.FSCTL_READ_USN_JOURNAL, inBuf, InSize, outBuf, OutSize,
                    out uint bytesReturned, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    return UsnDeltaResult.Unavailable(err switch
                    {
                        Native.ERROR_JOURNAL_ENTRY_DELETED => "журнал усечён — записи от сохранённого USN уже удалены",
                        Native.ERROR_JOURNAL_DELETE_IN_PROGRESS => "журнал в процессе удаления",
                        Native.ERROR_JOURNAL_NOT_ACTIVE => "журнал USN выключен",
                        _ => $"FSCTL_READ_USN_JOURNAL: ошибка {err}"
                    });
                }
                if (bytesReturned < 8)
                    return UsnDeltaResult.Unavailable("журнал вернул неполный ответ");

                Marshal.Copy(outBuf, managed, 0, (int)bytesReturned);
                if (!TryAppendRecords(managed, (int)bytesReturned, stopUsn, MaxRecords, records,
                        out long nextCursor, out string? anomaly))
                    return UsnDeltaResult.Unavailable(anomaly!);

                if (bytesReturned <= 8) break;   // записей больше нет (только указатель продолжения)
                if (nextCursor <= cursor) break; // защита от зацикливания: курсор обязан расти
                cursor = nextCursor;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        return Resolve(h, records);
    }

    /// <summary>
    /// Разбор одного буфера FSCTL_READ_USN_JOURNAL: первые 8 байт — USN продолжения, дальше записи.
    /// Метод чистый и публичный — покрывается тестами на буферах, собранных руками (без FSCTL).
    /// false → аномалия (запись версии ≠ V2, запись без имени, превышен кап maxRecords) — дельта
    /// недоступна ЦЕЛИКОМ, вызывающий делает полный пересбор.
    /// Записи с Usn ≥ stopUsn не добавляются: реконсайл гарантирован ровно до водяного знака
    /// stopUsn, хвост дочитает следующий скан (повторное применение идемпотентно — restat смотрит
    /// на реальное состояние диска).
    /// </summary>
    public static bool TryAppendRecords(byte[] buffer, int length, long stopUsn, int maxRecords,
        List<UsnDeltaRecord> records, out long nextCursor, out string? anomalyReason)
    {
        anomalyReason = null;
        if (length < 8 || length > buffer.Length)
        {
            nextCursor = 0;
            anomalyReason = "журнал вернул некорректную длину буфера";
            return false;
        }
        nextCursor = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));

        var span = buffer.AsSpan(0, length);
        int offset = 8;
        while (offset < span.Length)
        {
            if (!UsnRecordParser.TryParse(span, offset, out var rec))
            {
                anomalyReason = $"неполная или повреждённая запись журнала по смещению {offset}";
                return false;
            }
            if (rec.Major != 2)
            {
                anomalyReason = $"запись журнала неподдерживаемой версии V{rec.Major} — точечный реконсайл невозможен";
                return false;
            }
            if (rec.Name.Length == 0)
            {
                anomalyReason = "запись журнала без имени файла — путь не восстановить";
                return false;
            }
            if (rec.Usn < stopUsn)
            {
                if (records.Count >= maxRecords)
                {
                    anomalyReason = $"дельта больше {maxRecords:N0} записей — полный пересбор дешевле";
                    return false;
                }
                records.Add(new UsnDeltaRecord(rec.Frn, rec.ParentFrn, rec.Name, rec.Attributes, rec.Reason, rec.Usn));
            }
            offset += rec.Length;
        }
        return offset == span.Length;
    }

    /// <summary>
    /// Восстановление полных путей: для каждого УНИКАЛЬНОГО родительского FRN каталог открывается
    /// по id один раз (OpenFileById → GetFinalPathNameByHandle). Карта всей MFT не строится — это
    /// убило бы смысл дельты. Любой невосстановимый родитель → дельта недоступна целиком.
    /// </summary>
    private static UsnDeltaResult Resolve(SafeFileHandle volumeHandle, List<UsnDeltaRecord> records)
    {
        if (records.Count == 0) return UsnDeltaResult.Ok(Array.Empty<UsnDeltaChange>());

        var parentPaths = new Dictionary<ulong, string?>();
        var changes = new List<UsnDeltaChange>(records.Count);
        foreach (var r in records)
        {
            if (!parentPaths.TryGetValue(r.ParentFrn, out var dir))
            {
                dir = Native.TryGetPathByFrn(volumeHandle, r.ParentFrn);
                parentPaths[r.ParentFrn] = dir;
            }
            if (string.IsNullOrEmpty(dir))
                return UsnDeltaResult.Unavailable(
                    $"не восстановился путь родительского каталога записи «{r.FileName}» — безопаснее полный пересбор");

            string full = dir.EndsWith('\\') ? dir + r.FileName : dir + "\\" + r.FileName;
            changes.Add(new UsnDeltaChange(full, r.Reason, (r.Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0));
        }
        return UsnDeltaResult.Ok(changes);
    }
}
