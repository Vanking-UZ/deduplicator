using System.Management;
using System.Runtime.InteropServices;

namespace Deduplicator.Core.Services;

public enum DiskHealth { Unknown = 0, Healthy = 1, Warning = 2, Unhealthy = 3 }

/// <summary>Состояние здоровья физического диска (SMART/Storage WMI).</summary>
public sealed class DiskHealthInfo
{
    public string Name { get; set; } = "—";
    public string? Serial { get; set; }
    public string MediaType { get; set; } = "—";
    public string BusType { get; set; } = "—";
    public long SizeBytes { get; set; }
    public DiskHealth Health { get; set; } = DiskHealth.Unknown;
    public string? OperationalStatus { get; set; }

    public int? TemperatureC { get; set; }
    public long? PowerOnHours { get; set; }
    public int? WearPercent { get; set; }
    public long? ReadErrors { get; set; }
    public uint? SpindleRpm { get; set; }
    public bool PredictFailure { get; set; }
}

public interface IDiskHealthService
{
    bool IsSupported { get; }
    List<DiskHealthInfo> GetDisks();
}

/// <summary>
/// Здоровье дисков через WMI (root\Microsoft\Windows\Storage + root\WMI).
/// Деградирует мягко: если что-то недоступно (нет прав/драйвера), поле остаётся пустым.
/// </summary>
public sealed class DiskHealthService : IDiskHealthService
{
    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public List<DiskHealthInfo> GetDisks()
    {
        var result = new List<DiskHealthInfo>();
        if (!IsSupported) return result;

        bool anyPredictFailure = TryGetPredictFailure();

        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            using var col = searcher.Get();

            foreach (ManagementBaseObject moBase in col)
            using (moBase) // COM-обёртка — освобождаем каждую (не копим хэндлы)
            {
                if (moBase is not ManagementObject mo) continue;
                var info = new DiskHealthInfo
                {
                    Name = GetString(mo, "FriendlyName") ?? "Диск",
                    Serial = Trim(GetString(mo, "SerialNumber")),
                    SizeBytes = (long)(GetUInt64(mo, "Size") ?? 0),
                    MediaType = MediaTypeName(GetUInt16(mo, "MediaType")),
                    BusType = BusTypeName(GetUInt16(mo, "BusType")),
                    Health = HealthFrom(GetUInt16(mo, "HealthStatus")),
                    OperationalStatus = OperationalText(mo),
                    SpindleRpm = NonZero(GetUInt32(mo, "SpindleSpeed")),
                    PredictFailure = anyPredictFailure
                };

                FillReliability(mo, info);
                // #19: НЕ понижаем статус здоровых дисков из-за глобального флага предсказания отказа —
                // он не привязан к конкретному диску. Состояние берём из per-disk HealthStatus,
                // а общий флаг показывается отдельным баннером (AnyPredictFailure).

                result.Add(info);
            }
        }
        catch
        {
            // WMI недоступен — вернём то, что собрали (возможно, пусто).
        }

        return result;
    }

    private static void FillReliability(ManagementObject disk, DiskHealthInfo info)
    {
        try
        {
            using var outParams = disk.InvokeMethod("GetReliabilityCounter", null, null);
            if (outParams is null) return;

            ManagementBaseObject? rc = null;
            foreach (var prop in outParams.Properties)
            {
                if (prop.Value is ManagementBaseObject embedded) { rc = embedded; break; }
            }
            if (rc is null) return;

            using (rc) // вложенный объект — тоже COM-обёртка
            {
                info.TemperatureC = (int?)GetUInt16(rc, "Temperature");
                info.PowerOnHours = GetUInt64(rc, "PowerOnHours") is ulong poh ? (long)poh : null;
                info.WearPercent = (int?)GetUInt16(rc, "Wear");
                info.ReadErrors = GetUInt64(rc, "ReadErrorsTotal") is ulong re ? (long)re : null;
            }
        }
        catch
        {
            // Счётчики надёжности недоступны (часто нужны права администратора).
        }
    }

    private bool TryGetPredictFailure()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus"));
            using var col = searcher.Get();
            foreach (ManagementBaseObject mo in col)
            using (mo)
            {
                var pf = mo["PredictFailure"];
                if (pf is bool b && b) return true;
            }
        }
        catch { /* нет прав/не поддерживается */ }
        return false;
    }

    // --- helpers ---

    private static DiskHealth HealthFrom(ushort? status) => status switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown
    };

    private static string MediaTypeName(ushort? t) => t switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "—"
    };

    private static string BusTypeName(ushort? t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
        13 => "MMC", 15 => "File-backed", 16 => "Storage Spaces", 17 => "NVMe",
        _ => "—"
    };

    private static string? OperationalText(ManagementObject mo)
    {
        try
        {
            if (mo["OperationalStatus"] is ushort[] arr && arr.Length > 0)
                return string.Join(", ", arr);
        }
        catch { }
        return null;
    }

    private static string? GetString(ManagementBaseObject mo, string name)
    {
        try { return mo[name]?.ToString(); } catch { return null; }
    }
    private static ushort? GetUInt16(ManagementBaseObject mo, string name)
    {
        try { return mo[name] is null ? null : Convert.ToUInt16(mo[name]); } catch { return null; }
    }
    private static uint? GetUInt32(ManagementBaseObject mo, string name)
    {
        try { return mo[name] is null ? null : Convert.ToUInt32(mo[name]); } catch { return null; }
    }
    private static ulong? GetUInt64(ManagementBaseObject mo, string name)
    {
        try { return mo[name] is null ? null : Convert.ToUInt64(mo[name]); } catch { return null; }
    }
    private static uint? NonZero(uint? v) => v is > 0 ? v : null;
    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
