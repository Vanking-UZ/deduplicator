using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

/// <summary>
/// Определяет тип носителя (SSD vs HDD) через IOCTL_STORAGE_QUERY_PROPERTY
/// с дескриптором seek-penalty. На SSD «штрафа за позиционирование» нет.
/// Запрос свойств тома НЕ требует прав администратора (в отличие от MFT-чтения).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StorageDetector
{
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    public static StorageType Detect(string volume) // volume вида "C:"
    {
        if (!OperatingSystem.IsWindows()) return StorageType.Unknown;
        if (volume.Length != 2 || volume[1] != ':') return StorageType.Unknown;

        SafeFileHandle? handle = null;
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try
        {
            handle = Native.OpenVolumeQuery(@"\\.\" + volume);
            if (handle.IsInvalid) return StorageType.Unknown;

            // STORAGE_PROPERTY_QUERY: PropertyId(int) + QueryType(int) + 1 байт AdditionalParameters
            inBuf = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(inBuf, 0, StorageDeviceSeekPenaltyProperty);
            Marshal.WriteInt32(inBuf, 4, PropertyStandardQuery);
            Marshal.WriteByte(inBuf, 8, 0);

            // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version(uint) + Size(uint) + IncursSeekPenalty(BOOLEAN @ offset 8)
            outBuf = Marshal.AllocHGlobal(16);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(outBuf, i, 0);

            bool ok = Native.DeviceIoControl(
                handle,
                Native.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, 12, outBuf, 16, out _, IntPtr.Zero);
            if (!ok) return StorageType.Unknown;

            byte incursSeekPenalty = Marshal.ReadByte(outBuf, 8);
            return incursSeekPenalty == 0 ? StorageType.Ssd : StorageType.Hdd;
        }
        catch
        {
            return StorageType.Unknown;
        }
        finally
        {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            handle?.Dispose();
        }
    }
}
