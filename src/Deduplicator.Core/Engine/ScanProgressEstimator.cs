using System.Diagnostics;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Engine;

/// <summary>Логическая фаза общего прогресса. Она подробнее пользовательского <see cref="ScanStage"/>:
/// фото и аудио имеют отдельные диапазоны, поэтому общий процент не откатывается при втором
/// перцептивном проходе.</summary>
public enum ScanProgressPhase
{
    Enumerating,
    Exact,
    SimilarImages,
    SimilarAudio,
    SimilarGrouping,
    Finalizing
}

/// <summary>Чистый снимок оценки, который <c>ScanService</c> переносит в <see cref="ScanProgress"/>.</summary>
public sealed record ScanProgressEstimate(
    double? OverallFraction,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    double BytesPerSecond,
    double FilesPerSecond,
    long? EstimatedTotalFiles);

/// <summary>
/// Тестируемый оценщик общего прогресса и ETA. Процент монотонен, паузы исключаются из времени и
/// скоростей, а ETA не публикуется до появления достаточного сигнала. На первом обходе папки без
/// исторической оценки число файлов неизвестно: перечисление честно остаётся в начале своего
/// диапазона, а не рисует выдуманный процент.
/// </summary>
public sealed class ScanProgressEstimator
{
    private readonly object _sync = new();
    private readonly Func<TimeSpan> _clock;
    private readonly Dictionary<ScanProgressPhase, (double Start, double Width)> _ranges;
    private readonly TimeSpan _startedAt;

    private ScanProgressPhase _phase = ScanProgressPhase.Enumerating;
    private double _phaseFraction;
    private double _overall;
    private bool _hasOverallSignal;
    private long? _estimatedTotalFiles;

    private bool _paused;
    private bool _terminal;
    private TimeSpan _pauseStartedAt;
    private TimeSpan _pausedTotal;

    private bool _rateBaselineReady;
    private TimeSpan _lastRateAt;
    private long _lastBytes;
    private long _lastFiles;
    private double _bytesPerSecond;
    private double _filesPerSecond;

    private static readonly TimeSpan MinRateWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinEtaElapsed = TimeSpan.FromSeconds(2);
    private const double RateAlpha = 0.30;

    /// <param name="clock">
    /// Монотонные часы для тестов. По умолчанию используется <see cref="Stopwatch.GetTimestamp"/>.
    /// Возвращаемое значение может иметь любое начало отсчёта — важны только разности.
    /// </param>
    public ScanProgressEstimator(ScanDepth depth, long? estimatedTotalFiles = null,
        Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? MonotonicNow;
        _startedAt = _clock();
        _estimatedTotalFiles = PositiveOrNull(estimatedTotalFiles);
        // Историческое число файлов полезно как подпись «примерно N», но не является надёжным
        // знаменателем: после больших изменений оно могло бы преждевременно довести этап до 98%.
        _hasOverallSignal = false;
        _ranges = BuildRanges(depth);
    }

    public void SetEstimatedTotalFiles(long? value)
    {
        lock (_sync)
        {
            _estimatedTotalFiles = PositiveOrNull(value);
        }
    }

    public ScanProgressEstimate BeginPhase(ScanProgressPhase phase, long filesSeen,
        long filesProcessed, long bytesProcessed)
    {
        lock (_sync)
        {
            _phase = phase;
            _phaseFraction = 0;
            if (phase != ScanProgressPhase.Enumerating) _hasOverallSignal = true;
            AdvanceOverallUnsafe(Range(phase).Start);
            return UpdateUnsafe(filesSeen, filesProcessed, bytesProcessed, null);
        }
    }

    public ScanProgressEstimate Update(long filesSeen, long filesProcessed, long bytesProcessed,
        double? phaseFraction = null)
    {
        lock (_sync)
            return UpdateUnsafe(filesSeen, filesProcessed, bytesProcessed, phaseFraction);
    }

    public ScanProgressEstimate CompletePhase(long filesSeen, long filesProcessed, long bytesProcessed)
    {
        lock (_sync)
        {
            _phaseFraction = 1;
            _hasOverallSignal = true;
            var range = Range(_phase);
            AdvanceOverallUnsafe(range.Start + range.Width);
            return UpdateUnsafe(filesSeen, filesProcessed, bytesProcessed, 1);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_paused || _terminal) return;
            _paused = true;
            _pauseStartedAt = _clock();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_paused || _terminal) return;
            var now = _clock();
            if (now > _pauseStartedAt) _pausedTotal += now - _pauseStartedAt;
            _paused = false;
            // Первый тик после паузы только задаёт новую базу: время простоя не разбавляет скорость.
            _rateBaselineReady = false;
        }
    }

    public ScanProgressEstimate Finish(bool completed, long filesSeen, long filesProcessed,
        long bytesProcessed)
    {
        lock (_sync)
        {
            if (_paused)
            {
                var now = _clock();
                if (now > _pauseStartedAt) _pausedTotal += now - _pauseStartedAt;
                _paused = false;
            }
            _terminal = true;
            if (completed) { _overall = 1; _hasOverallSignal = true; }
            var snapshot = UpdateUnsafe(filesSeen, filesProcessed, bytesProcessed, null);
            return snapshot with { EstimatedRemaining = null };
        }
    }

    private ScanProgressEstimate UpdateUnsafe(long filesSeen, long filesProcessed, long bytesProcessed,
        double? phaseFraction)
    {
        if (filesSeen > 0 && _estimatedTotalFiles.HasValue && filesSeen > _estimatedTotalFiles.Value)
        {
            // Историческая оценка может уточняться вверх, но при полном отсутствии исходной оценки
            // не выводим total из seen: seen*1.05 мгновенно нарисовал бы ложные ~98% перечисления.
            _estimatedTotalFiles = Math.Max(filesSeen, (long)Math.Ceiling(filesSeen * 1.05));
        }

        // У перечисления нет гарантированного total без отдельного полного pre-count (который удвоил
        // бы I/O). Исторический count показываем как оценку, но общий процент остаётся неопределённым
        // до завершения этапа. Остальные фазы передают явную долю работы.
        double? effectiveFraction = phaseFraction;

        if (effectiveFraction.HasValue)
        {
            _hasOverallSignal = true;
            // 2% диапазона остаются на безопасное завершение/flush текущей фазы.
            _phaseFraction = Math.Clamp(effectiveFraction.Value, 0, 1);
            var visible = _phaseFraction >= 1 ? 1 : Math.Min(0.98, _phaseFraction);
            var range = Range(_phase);
            AdvanceOverallUnsafe(range.Start + range.Width * visible);
        }

        var elapsed = ActiveElapsedUnsafe();
        UpdateRatesUnsafe(elapsed, filesProcessed, bytesProcessed);
        return SnapshotUnsafe(elapsed);
    }

    private void UpdateRatesUnsafe(TimeSpan elapsed, long filesProcessed, long bytesProcessed)
    {
        filesProcessed = Math.Max(0, filesProcessed);
        bytesProcessed = Math.Max(0, bytesProcessed);
        if (_terminal || _paused) return;

        if (!_rateBaselineReady || filesProcessed < _lastFiles || bytesProcessed < _lastBytes)
        {
            _lastRateAt = elapsed;
            _lastFiles = filesProcessed;
            _lastBytes = bytesProcessed;
            _rateBaselineReady = true;
            return;
        }

        var dt = elapsed - _lastRateAt;
        if (dt < MinRateWindow || dt <= TimeSpan.Zero) return;

        long fileDelta = Math.Max(0, filesProcessed - _lastFiles);
        long byteDelta = Math.Max(0, bytesProcessed - _lastBytes);
        // Heartbeat обновляет elapsed/ETA, но не должен сдвигать базу скорости без новой работы:
        // иначе многогигабайтный файл после минуты чтения делился бы только на последнюю секунду.
        if (fileDelta == 0 && byteDelta == 0) return;

        double seconds = dt.TotalSeconds;
        double instantFiles = fileDelta / seconds;
        double instantBytes = byteDelta / seconds;
        _filesPerSecond = Smooth(_filesPerSecond, instantFiles);
        _bytesPerSecond = Smooth(_bytesPerSecond, instantBytes);
        _lastRateAt = elapsed;
        _lastFiles = filesProcessed;
        _lastBytes = bytesProcessed;
    }

    private ScanProgressEstimate SnapshotUnsafe(TimeSpan elapsed)
    {
        TimeSpan? remaining = null;
        if (_hasOverallSignal && !_terminal && !_paused && elapsed >= MinEtaElapsed &&
            _overall >= 0.01 && _overall < 1)
        {
            double seconds = elapsed.TotalSeconds * (1 - _overall) / _overall;
            // Защита UI/сериализации от бесконечностей и бессмысленных многолетних значений.
            if (double.IsFinite(seconds) && seconds >= 0)
                remaining = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(365).TotalSeconds));
        }

        return new ScanProgressEstimate(_hasOverallSignal ? _overall : null, elapsed, remaining,
            _bytesPerSecond, _filesPerSecond, _estimatedTotalFiles);
    }

    private TimeSpan ActiveElapsedUnsafe()
    {
        var end = _paused ? _pauseStartedAt : _clock();
        var elapsed = end - _startedAt - _pausedTotal;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private (double Start, double Width) Range(ScanProgressPhase phase) =>
        _ranges.TryGetValue(phase, out var value) ? value : (Math.Min(_overall, 0.99), 0);

    private void AdvanceOverallUnsafe(double value) =>
        _overall = Math.Max(_overall, Math.Clamp(value, 0, 1));

    private static double Smooth(double previous, double current) =>
        previous <= 0 ? current : RateAlpha * current + (1 - RateAlpha) * previous;

    private static long? PositiveOrNull(long? value) => value is > 0 ? value : null;

    private static TimeSpan MonotonicNow() =>
        TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    private static Dictionary<ScanProgressPhase, (double Start, double Width)> BuildRanges(ScanDepth depth)
    {
        // Сумма всегда 1. Последние 2% оставлены для атомарных финальных записей/проверок.
        return depth switch
        {
            ScanDepth.WithSimilarImagesAndAudio => new()
            {
                [ScanProgressPhase.Enumerating] = (0.00, 0.15),
                [ScanProgressPhase.Exact] = (0.15, 0.40),
                [ScanProgressPhase.SimilarImages] = (0.55, 0.18),
                [ScanProgressPhase.SimilarAudio] = (0.73, 0.17),
                [ScanProgressPhase.SimilarGrouping] = (0.90, 0.08),
                [ScanProgressPhase.Finalizing] = (0.98, 0.02)
            },
            ScanDepth.WithSimilarImages => new()
            {
                [ScanProgressPhase.Enumerating] = (0.00, 0.20),
                [ScanProgressPhase.Exact] = (0.20, 0.45),
                [ScanProgressPhase.SimilarImages] = (0.65, 0.25),
                [ScanProgressPhase.SimilarAudio] = (0.90, 0.00),
                [ScanProgressPhase.SimilarGrouping] = (0.90, 0.08),
                [ScanProgressPhase.Finalizing] = (0.98, 0.02)
            },
            _ => new()
            {
                [ScanProgressPhase.Enumerating] = (0.00, 0.30),
                [ScanProgressPhase.Exact] = (0.30, 0.68),
                [ScanProgressPhase.SimilarImages] = (0.98, 0.00),
                [ScanProgressPhase.SimilarAudio] = (0.98, 0.00),
                [ScanProgressPhase.SimilarGrouping] = (0.98, 0.00),
                [ScanProgressPhase.Finalizing] = (0.98, 0.02)
            }
        };
    }
}
