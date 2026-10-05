namespace Deduplicator.Core.Engine;

/// <summary>Бюджет перцептивного конвейера: сколько байтов держать «в полёте» между чтением
/// и декодом и сколько декодеров запускать (этапы 2.4/2.6).</summary>
public sealed record PipelineBudget(long ChannelBytes, int DecoderDop);

/// <summary>
/// Губернатор ресурсов (этап 2.6): слабый ПК не должен уходить в своп из-за конвейера
/// чтение→декод. Чистая логика (тестируется без окружения) + тонкая обёртка над
/// GC.GetGCMemoryInfo. Бюджет пересчитывается на старте КАЖДОЙ страницы перцептивного
/// этапа — не на каждый файл (дёшево и достаточно оперативно).
/// </summary>
public static class ResourceGovernor
{
    /// <summary>Обычный бюджет канала чтение→декод (~128 МБ).</summary>
    public const long NormalChannelBytes = 128L * 1024 * 1024;

    /// <summary>Сжатый бюджет канала при высокой загрузке памяти (32 МБ).</summary>
    public const long ReducedChannelBytes = 32L * 1024 * 1024;

    /// <summary>Число декодеров при высокой загрузке памяти.</summary>
    public const int ReducedDecoderDop = 2;

    /// <summary>
    /// Чистая функция бюджета: при высокой загрузке памяти (memoryLoad ≥ highThreshold) канал
    /// сжимается до 32 МБ и декодеров остаётся 2, иначе — 128 МБ и по декодеру на ядро.
    /// totalAvailable — страховка для машин с крошечной памятью: канал не больше четверти
    /// всей доступной памяти (контейнеры/1-ГБ конфигурации).
    /// </summary>
    public static PipelineBudget ComputeBudget(long totalAvailable, long memoryLoad,
        long highThreshold, int processorCount)
    {
        int normalDop = Math.Max(1, processorCount);
        bool high = highThreshold > 0 && memoryLoad >= highThreshold;
        long channel = high ? ReducedChannelBytes : NormalChannelBytes;
        if (totalAvailable > 0) channel = Math.Min(channel, Math.Max(1, totalAvailable / 4));
        return new PipelineBudget(channel, high ? Math.Min(ReducedDecoderDop, normalDop) : normalDop);
    }

    /// <summary>Обёртка для боевого кода: текущий бюджет по GC.GetGCMemoryInfo.</summary>
    public static PipelineBudget CurrentBudget()
    {
        var info = GC.GetGCMemoryInfo();
        return ComputeBudget(info.TotalAvailableMemoryBytes, info.MemoryLoadBytes,
            info.HighMemoryLoadThresholdBytes, Environment.ProcessorCount);
    }
}

/// <summary>
/// Экспоненциально сглаженная пропускная способность чтения (МБ/с). Продюсеры конвейера меряют
/// каждое чтение; сглаживание гасит выбросы (кэш ОС, единичный мелкий файл). Потокобезопасен:
/// несколько продюсеров одного тома обновляют одно значение. Живёт только в рамках одного
/// перцептивного этапа — глобального состояния между сканами нет.
/// </summary>
public sealed class ThroughputEwma
{
    private readonly object _lock = new();
    private readonly double _alpha;
    private double _value;
    private bool _seeded;

    /// <param name="alpha">Вес нового замера (0..1]: больше — быстрее реагирует, меньше — сильнее сглаживает.</param>
    public ThroughputEwma(double alpha = 0.3)
    {
        if (alpha <= 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        _alpha = alpha;
    }

    /// <summary>Сглаженная скорость, МБ/с. 0 — замеров ещё не было.</summary>
    public double Value { get { lock (_lock) return _value; } }

    /// <summary>Учесть замер: bytes прочитано за elapsedSeconds. Пустые/нулевые замеры игнорируются.</summary>
    public void Update(long bytes, double elapsedSeconds)
    {
        if (bytes <= 0 || elapsedSeconds <= 0) return;
        double mbps = bytes / elapsedSeconds / (1024.0 * 1024.0);
        lock (_lock)
        {
            _value = _seeded ? _alpha * mbps + (1 - _alpha) * _value : mbps;
            _seeded = true;
        }
    }
}

/// <summary>Чистые решения губернатора (тестируются без диска и таймеров).</summary>
public static class GovernorDecisions
{
    /// <summary>Ниже этой устойчивой скорости (МБ/с) параллельное чтение вредит: USB2 (~35 МБ/с) и сеть.</summary>
    public const double SlowMediaMBps = 45.0;

    /// <summary>
    /// Клэмп DOP чтения СЛЕДУЮЩЕЙ страницы по замеренной скорости текущей:
    /// HDD (hasSeekPenalty) не трогаем — план тома уже читает его последовательно (DOP=1);
    /// «SSD/неизвестно» с устойчивой скоростью ниже 45 МБ/с — на деле USB2/сеть → 1;
    /// быстрый SSD — без изменений. ewmaMBps ≤ 0 означает «замеров нет» — тоже без изменений.
    /// </summary>
    public static int ClampDop(int currentDop, double ewmaMBps, bool hasSeekPenalty)
    {
        if (hasSeekPenalty) return currentDop;
        if (ewmaMBps > 0 && ewmaMBps < SlowMediaMBps) return 1;
        return currentDop;
    }
}
