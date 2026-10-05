namespace FastDedup.Engine;

/// <summary>
/// Публичные хелперы окружения для UI/CLI: проверка прав и тип носителя.
/// (Native и StorageDetector внутренние — это их безопасная витрина наружу.)
/// </summary>
public static class EngineEnvironment
{
    /// <summary>Запущены ли мы с правами администратора (нужно для MFT-режима).</summary>
    public static bool IsElevated()
        => OperatingSystem.IsWindows() && Native.IsElevated();

    /// <summary>Тип носителя для тома вида "C:". Не требует прав администратора.</summary>
    public static StorageType DetectStorage(string volume)
        => OperatingSystem.IsWindows() ? StorageDetector.Detect(volume) : StorageType.Unknown;
}
