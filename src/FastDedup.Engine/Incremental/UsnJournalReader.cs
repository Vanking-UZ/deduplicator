using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>Снимок состояния USN-журнала тома (для инкрементального скана).</summary>
public readonly record struct UsnJournalSnapshot(ulong JournalId, long FirstUsn, long NextUsn);

/// <summary>
/// Чтение состояния USN-журнала через FSCTL_QUERY_USN_JOURNAL. Требует прав администратора и NTFS.
/// При ЛЮБОЙ ошибке (нет прав, не NTFS, журнал выключен, сетевой диск) возвращает false — и
/// вызывающий безопасно делает полный пересбор.
///
/// ВНИМАНИЕ: этот нативный путь НЕ проверен в текущем окружении (тесты идут без прав администратора →
/// журнал недоступен → ветка не активируется). Логика выбора инкрементальности покрыта тестами
/// отдельно (см. IncrementalScanPlannerTests); здесь — только тонкая обёртка над FSCTL.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsnJournalReader
{
    public static bool TryQuery(string volumeRoot, out UsnJournalSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            string drive = VolumeOf(volumeRoot);                  // "C:"
            using var h = Native.OpenVolumeRead(@"\\.\" + drive); // нужны права администратора
            if (h.IsInvalid) return false;

            const int Size = 56; // USN_JOURNAL_DATA_V0
            IntPtr outBuf = Marshal.AllocHGlobal(Size);
            try
            {
                bool ok = Native.DeviceIoControl(
                    h, Native.FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, outBuf, Size,
                    out uint bytes, IntPtr.Zero);
                if (!ok || bytes < 24) return false;

                var buf = new byte[24];
                Marshal.Copy(outBuf, buf, 0, 24);
                ulong journalId = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0));
                long firstUsn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
                long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));

                // Санити: журнал должен быть осмысленным, иначе не доверяем.
                if (journalId == 0 || nextUsn < 0 || firstUsn < 0 || firstUsn > nextUsn) return false;

                snapshot = new UsnJournalSnapshot(journalId, firstUsn, nextUsn);
                return true;
            }
            finally { Marshal.FreeHGlobal(outBuf); }
        }
        catch
        {
            return false; // нет прав/не NTFS/журнал выключен → полный пересбор
        }
    }

    /// <summary>«C:» из любого пути тома. internal — переиспользуется дельта-чтением журнала.</summary>
    internal static string VolumeOf(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is { Length: >= 2 } && root[1] == ':') return root.Substring(0, 2);
        throw new NotSupportedException("USN journal: поддерживаются только локальные тома с буквой диска");
    }
}
