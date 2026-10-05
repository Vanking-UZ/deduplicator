using System.Diagnostics;

namespace Deduplicator.Core.Engine;

/// <summary>
/// Сглаживает поток прогресса: вместо одного UI-события на файл отдаёт обновления не чаще
/// заданного интервала (по умолчанию ~8/сек). Смена этапа и терминальные состояния отдаются
/// немедленно (immediate). Лишние обновления между «тиками» схлопываются в последнее значение
/// (trailing-эмиссия через таймер), поэтому на 1M файлов UI получает сотни событий, а не миллионы.
/// </summary>
public sealed class ProgressCoalescer<T> : IDisposable
{
    private readonly Action<T> _sink;
    private readonly long _intervalTicks;
    private readonly object _lock = new();
    private readonly Timer _timer;

    private T _pending = default!;
    private bool _hasPending;
    private bool _timerArmed;
    private long _lastEmit;
    private bool _disposed;

    // Гард порядка доставки: trailing-колбэк таймера, снявший снимок ДО
    // терминального immediate-события, мог доставить устаревший снимок ПОСЛЕ него (вытеснение
    // между выходом из _lock и вызовом _sink) — UI после отмены/завершения перезаписывался
    // старым этапом. Номер снимка выдаётся под _lock, доставка сериализуется под _emitLock,
    // снимки со «старым» номером отбрасываются.
    private long _seq;
    private long _lastDelivered;
    private readonly object _emitLock = new();

    private void Emit(T value, long seq)
    {
        lock (_emitLock)
        {
            if (seq <= _lastDelivered) return; // более новый снимок уже доставлен
            _lastDelivered = seq;
            _sink(value);
        }
    }

    public ProgressCoalescer(Action<T> sink, TimeSpan? interval = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        var iv = interval ?? TimeSpan.FromMilliseconds(125);
        _intervalTicks = Math.Max(1, (long)(iv.TotalSeconds * Stopwatch.Frequency));
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        // Первый Report должен отдаться сразу: делаем вид, что последняя эмиссия была интервал назад.
        _lastEmit = Stopwatch.GetTimestamp() - _intervalTicks;
    }

    /// <summary>Сообщить о прогрессе. immediate=true — отдать немедленно (смена этапа, завершение, ошибка).</summary>
    public void Report(T value, bool immediate = false)
    {
        T toEmit = default!;
        long seq = 0;
        bool emit = false;
        lock (_lock)
        {
            if (_disposed) return;
            _pending = value;
            _hasPending = true;

            long now = Stopwatch.GetTimestamp();
            if (immediate || now - _lastEmit >= _intervalTicks)
            {
                toEmit = _pending;
                seq = ++_seq;
                emit = true;
                _hasPending = false;
                _lastEmit = now;
                DisarmTimer(); // снимаем взведённый trailing-таймер: иначе он мог бы выстрелить после терминала
            }
            else if (!_timerArmed)
            {
                _timerArmed = true;
                long remainTicks = _intervalTicks - (now - _lastEmit);
                long remainMs = Math.Max(1, remainTicks * 1000 / Stopwatch.Frequency);
                _timer.Change(remainMs, Timeout.Infinite);
            }
        }
        if (emit) Emit(toEmit, seq); // вне _lock: sink не должен держать наш лок
    }

    private void OnTimer()
    {
        T toEmit = default!;
        long seq = 0;
        bool emit = false;
        lock (_lock)
        {
            _timerArmed = false;
            if (_disposed || !_hasPending) return;
            toEmit = _pending;
            seq = ++_seq;
            emit = true;
            _hasPending = false;
            _lastEmit = Stopwatch.GetTimestamp();
        }
        if (emit) Emit(toEmit, seq);
    }

    /// <summary>Немедленно отдать последнее накопленное значение (вызывать в конце скана).</summary>
    public void Flush()
    {
        T toEmit = default!;
        long seq = 0;
        bool emit = false;
        lock (_lock)
        {
            if (_disposed) return;
            DisarmTimer();
            if (_hasPending)
            {
                toEmit = _pending;
                seq = ++_seq;
                emit = true;
                _hasPending = false;
                _lastEmit = Stopwatch.GetTimestamp();
            }
        }
        if (emit) Emit(toEmit, seq);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DisarmTimer();
        }
        // Дожидаемся завершения возможного callback-а таймера: иначе поздний _sink мог бы
        // выстрелить уже после терминального события и после обнуления состояния в ScanService.
        using var done = new ManualResetEvent(false);
        if (_timer.Dispose(done)) done.WaitOne();
    }

    /// <summary>Снять взведённый таймер. Вызывать только под _lock.</summary>
    private void DisarmTimer()
    {
        if (_timerArmed)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timerArmed = false;
        }
    }
}
