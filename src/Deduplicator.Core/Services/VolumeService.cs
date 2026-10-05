using System.Runtime.InteropServices;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Services;

public interface IVolumeService
{
    /// <summary>Подключённые сейчас носители (из ОС, ещё без привязки к каталогу).</summary>
    List<VolumeRecord> GetConnectedVolumes();

    /// <summary>
    /// Синхронизировать подключённые носители с каталогом БД и вернуть полный список
    /// (включая отключённые) с проставленным статусом подключения (FR-1..FR-3).
    /// </summary>
    List<VolumeRecord> SyncAndGetCatalog();

    /// <summary>Зарегистрировать/обновить том и получить его Id.</summary>
    long Register(VolumeRecord volume);
}

/// <summary>Обнаружение и идентификация носителей по серийному номеру тома.</summary>
public sealed class VolumeService : IVolumeService
{
    private readonly record struct DriveTopology(uint DiskNumber, int MediaType);

    private readonly VolumeRepository _volumes;
    public VolumeService(VolumeRepository volumes) => _volumes = volumes;

    public List<VolumeRecord> GetConnectedVolumes()
    {
        var result = new List<VolumeRecord>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        var topologyByLetter = GetDriveTopology();

        foreach (var d in drives)
        {
            // P1.4: показываем и сетевые/подключённые диски — «быстрый скан на всех дисках».
            // Бэкенд на этапе скана сам выберет совместимый сетевой режим (Win32FindNetwork).
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;

            VolumeRecord? rec;
            if (d.DriveType == DriveType.Network)
            {
                // Полумёртвый сетевой диск (хост недоступен, но маппинг жив) делает IsReady/serial/size
                // синхронными SMB-запросами на десятки секунд. Ограничиваем по времени, чтобы ОДИН такой
                // шар не тормозил обнаружение остальных дисков (NFR-1).
                rec = TryBuildWithTimeout(() => BuildRecord(d, topologyByLetter), TimeSpan.FromSeconds(3));
            }
            else
            {
                try { rec = BuildRecord(d, topologyByLetter); }
                catch { rec = null; } // недоступный носитель пропускаем — не валим перечисление (NFR-1)
            }
            if (rec is not null) result.Add(rec);
        }
        return result;
    }

    /// <summary>Собрать запись о диске (может делать синхронные FS-запросы). null — диск не готов.</summary>
    private VolumeRecord? BuildRecord(DriveInfo d, Dictionary<char, DriveTopology> topologyByLetter)
    {
        if (!d.IsReady) return null;
        var root = d.RootDirectory.FullName;
        bool isNetwork = d.DriveType == DriveType.Network;
        DriveTopology? topology = root.Length > 0 &&
                                  topologyByLetter.TryGetValue(char.ToUpperInvariant(root[0]), out var t)
            ? t
            : null;
        int media = topology?.MediaType ?? 0;

        // 1.2: seek-penalty IOCTL — первичный сигнал «механика vs флэш» (без прав администратора).
        // Точнее WMI: USB3-SSD/флешка → нет штрафа → параллельные чтения; HDD в USB-боксе,
        // который WMI считает «Fixed unknown», → штраф есть → последовательные.
        bool? seekPenalty = null;
        if (!isNetwork && root.Length >= 2 && root[1] == ':')
        {
            seekPenalty = FastDedup.Engine.EngineEnvironment.DetectStorage(root[..2]) switch
            {
                FastDedup.Engine.StorageType.Hdd => true,
                FastDedup.Engine.StorageType.Ssd => false,
                _ => null
            };
        }

        string serial = VolumeIdentityProbe.TryGetSerial(root) ?? root;
        string physicalKey = isNetwork
            ? "network:" + root.TrimEnd('\\', '/').ToUpperInvariant()
            : topology is { } local
                ? "disk:" + local.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "volume:" + serial.Trim().ToUpperInvariant();

        return new VolumeRecord
        {
            Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : d.VolumeLabel,
            Serial = serial,
            FsType = SafeGet(() => d.DriveFormat),
            TotalBytes = SafeGet(() => d.TotalSize),
            FreeBytes = SafeGet(() => d.AvailableFreeSpace),
            IsConnected = true,
            MountPath = root,
            IsRemovable = d.DriveType == DriveType.Removable,
            IsNetwork = isNetwork,
            MediaType = media,
            HasSeekPenalty = seekPenalty,
            PhysicalDiskNumber = topology?.DiskNumber,
            PhysicalDeviceKey = physicalKey
        };
    }

    /// <summary>Выполнить сбор записи с таймаутом; не дождались (мёртвый сетевой шар) → null.</summary>
    private static VolumeRecord? TryBuildWithTimeout(Func<VolumeRecord?> build, TimeSpan timeout)
    {
        try
        {
            var task = System.Threading.Tasks.Task.Run(build);
            return task.Wait(timeout) ? task.Result : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Сопоставление буквы с физическим DiskNumber и типом носителя через WMI Storage. DiskNumber
    /// сохраняем даже когда MediaType неизвестен: именно он не даёт двум разделам HDD читаться вместе.
    /// </summary>
    private static Dictionary<char, DriveTopology> GetDriveTopology()
    {
        var result = new Dictionary<char, DriveTopology>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return result;
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            // Диск № -> тип носителя (3 HDD, 4 SSD). Каждый ManagementBaseObject — COM-обёртка,
            // её нужно освобождать явно (using), иначе на горячем пути копятся хэндлы.
            var diskMedia = new Dictionary<uint, int>();
            using (var pd = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk")))
            using (var pdCol = pd.Get())
            {
                foreach (System.Management.ManagementBaseObject o in pdCol)
                using (o)
                {
                    if (uint.TryParse(o["DeviceId"]?.ToString(), out var devId))
                        diskMedia[devId] = o["MediaType"] is null ? 0 : Convert.ToInt32(o["MediaType"]);
                }
            }

            // Буква диска -> диск № -> тип носителя.
            using (var part = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
            using (var partCol = part.Get())
            {
                foreach (System.Management.ManagementBaseObject o in partCol)
                using (o)
                {
                    var letterObj = o["DriveLetter"];
                    if (letterObj is null) continue;
                    char letter = Convert.ToChar(letterObj);
                    if (letter == '\0') continue;
                    if (uint.TryParse(o["DiskNumber"]?.ToString(), out var diskNo))
                    {
                        diskMedia.TryGetValue(diskNo, out var media);
                        result[char.ToUpperInvariant(letter)] = new DriveTopology(diskNo, media);
                    }
                }
            }
        }
        catch { /* WMI недоступно — параллелизм по настройке */ }
        return result;
    }

    public List<VolumeRecord> SyncAndGetCatalog()
    {
        var connected = GetConnectedVolumes();
        var connectedBySerial = new Dictionary<string, VolumeRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in connected)
        {
            if (string.IsNullOrEmpty(c.Serial)) continue;
            connectedBySerial[c.Serial] = c;
            // Регистрируем/обновляем в каталоге.
            c.Id = _volumes.Upsert(c);
        }

        var catalog = _volumes.GetAll();
        foreach (var v in catalog)
        {
            if (v.Serial is not null && connectedBySerial.TryGetValue(v.Serial, out var c))
            {
                v.IsConnected = true;
                v.MountPath = c.MountPath;
                v.FreeBytes = c.FreeBytes;
                v.IsRemovable = c.IsRemovable;
                v.IsNetwork = c.IsNetwork; // Без этого сетевой диск читался параллельно
                v.MediaType = c.MediaType;
                v.HasSeekPenalty = c.HasSeekPenalty; // 1.2: сигнал IOCTL обязан пережить merge (как IsNetwork)
                v.PhysicalDiskNumber = c.PhysicalDiskNumber;
                v.PhysicalDeviceKey = c.PhysicalDeviceKey;
                if (v.TotalBytes == 0) v.TotalBytes = c.TotalBytes;
            }
            else
            {
                v.IsConnected = false;
            }
        }
        return catalog;
    }

    public long Register(VolumeRecord volume) => _volumes.Upsert(volume);

    private static T SafeGet<T>(Func<T> get)
    {
        try { return get(); } catch { return default!; }
    }

}
