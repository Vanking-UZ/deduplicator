namespace Deduplicator.Core.Services;

public interface IDriveWatcher
{
    /// <summary>Срабатывает при подключении/извлечении носителя (в фоновом потоке).</summary>
    event Action? DrivesChanged;
    void Start();
    void Stop();
}

/// <summary>
/// Следит за подключением/извлечением носителей опросом раз в ~2 секунды и оповещает,
/// когда набор готовых дисков меняется (реальное время для каталога).
/// </summary>
public sealed class DriveWatcher : IDriveWatcher, IDisposable
{
    private readonly System.Threading.Timer _timer;
    private string _signature = "";
    private bool _started;

    public event Action? DrivesChanged;

    public DriveWatcher()
    {
        _timer = new System.Threading.Timer(_ => Check(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _signature = Signature();
        _timer.Change(2000, 2000);
    }

    public void Stop() => _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    private void Check()
    {
        var sig = Signature();
        if (sig == _signature) return;
        _signature = sig;
        try { DrivesChanged?.Invoke(); } catch { }
    }

    private static string Signature()
    {
        try
        {
            // Сетевые диски участвуют в сигнатуре ДЕШЁВО: только буква + тип (без IsReady/TotalSize —
            // это синхронные SMB-запросы, полумёртвая шара повесила бы опрос). Иначе подключение/обрыв
            // сетевого диска не рождало DrivesChanged и экраны показывали устаревший статус.
            var parts = DriveInfo.GetDrives()
                .Where(d => { try { return d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network; } catch { return false; } })
                .Select(d =>
                {
                    try
                    {
                        if (d.DriveType == DriveType.Network) return d.Name + "|net";
                        return d.IsReady ? d.Name + "|" + d.TotalSize : null;
                    }
                    catch { return d.Name; }
                })
                .Where(s => s is not null)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts);
        }
        catch
        {
            return "";
        }
    }

    public void Dispose() => _timer.Dispose();
}
