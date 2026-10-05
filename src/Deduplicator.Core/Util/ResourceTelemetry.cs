using System.Diagnostics;

namespace Deduplicator.Core.Util;

/// <summary>
/// Сэмплер ресурсов процесса на время скана (этап 1.1): рабочее множество / managed-куча /
/// число потоков раз в ~5 секунд в лог, пики — итоговой строкой при завершении. Это база для
/// диагностики «умирает на слабом ПК» и для честных «до/после» всех перф-работ.
/// </summary>
public sealed class ResourceTelemetry : IDisposable
{
    private readonly string _label;
    private readonly Timer _timer;
    private long _peakWorkingSet;
    private long _peakHeap;
    private int _peakThreads;
    private bool _disposed;

    public ResourceTelemetry(string label, TimeSpan? interval = null)
    {
        _label = label;
        var iv = interval ?? TimeSpan.FromSeconds(5);
        Sample(log: false);
        _timer = new Timer(_ => { try { Sample(log: true); } catch { } }, null, iv, iv);
    }

    private void Sample(bool log)
    {
        long ws = Environment.WorkingSet;
        long heap = GC.GetTotalMemory(forceFullCollection: false);
        int threads;
        using (var p = Process.GetCurrentProcess()) threads = p.Threads.Count;

        if (ws > _peakWorkingSet) _peakWorkingSet = ws;
        if (heap > _peakHeap) _peakHeap = heap;
        if (threads > _peakThreads) _peakThreads = threads;

        if (log)
            AppLog.Write($"Ресурсы[{_label}]: WS={ws / (1 << 20)} МБ, heap={heap / (1 << 20)} МБ, потоков={threads}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using var done = new ManualResetEvent(false);
        if (_timer.Dispose(done)) done.WaitOne();
        try { Sample(log: false); } catch { }
        AppLog.Write($"Ресурсы[{_label}] ПИКИ: WS={_peakWorkingSet / (1 << 20)} МБ, " +
                     $"heap={_peakHeap / (1 << 20)} МБ, потоков={_peakThreads}");
    }
}
