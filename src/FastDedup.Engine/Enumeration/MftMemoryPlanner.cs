namespace FastDedup.Engine;

/// <summary>
/// RAM-aware решение «читать ли MFT в память». <c>UsnEnumerator.ReadMap</c> строит Dictionary всей
/// MFT (~120+ байт на запись с именем); на машине с 2–4 ГБ RAM том на 5–10 млн записей уводит в
/// своп, и «турбо»-режим становится МЕДЛЕННЕЕ совместимого. Чистая функция — полностью тестируема;
/// сам FSCTL-запрос размера MFT живёт в <c>Native.TryGetMftValidDataLength</c>.
/// </summary>
public static class MftMemoryPlanner
{
    /// <summary>Размер одной записи MFT на диске (FILE_RECORD_SEGMENT, стандартно 1 КБ).</summary>
    private const long MftBytesPerRecord = 1024;

    /// <summary>Прогноз памяти на запись карты: слот Dictionary + Rec + строка имени. Консервативно.</summary>
    private const long MapBytesPerRecord = 200;

    /// <summary>Карте отдаём не больше четверти доступной памяти — остальное нужно самому скану.</summary>
    private const double MaxAvailableShare = 0.25;

    /// <summary>Прогноз размера карты MFT в памяти по MftValidDataLength (байты $MFT на диске).</summary>
    public static long EstimateMapBytes(long mftValidDataLength)
        => Math.Max(0, mftValidDataLength) / MftBytesPerRecord * MapBytesPerRecord;

    /// <summary>
    /// true — прогноз карты MFT помещается в бюджет (≤ 25% доступной памяти), можно читать.
    /// Неизвестные/бессмысленные входы (≤ 0) трактуем как «читать» — поведение как до этой проверки.
    /// </summary>
    public static bool ShouldUseMft(long mftValidDataLength, long totalAvailableMemory)
    {
        if (mftValidDataLength <= 0 || totalAvailableMemory <= 0) return true;
        return EstimateMapBytes(mftValidDataLength) <= (long)(totalAvailableMemory * MaxAvailableShare);
    }
}
