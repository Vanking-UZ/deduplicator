namespace Deduplicator.Core.Models;

/// <summary>
/// Совместимое преобразование простых пользовательских режимов нагрузки
/// в уже существующий параметр параллелизма сканирования.
/// </summary>
public static class PerformanceProfiles
{
    public const string Automatic = "Automatic";
    public const string Gentle = "Gentle";
    public const string Maximum = "Maximum";

    public static string Normalize(string? value, bool legacyEcoMode = false) => value switch
    {
        Gentle => Gentle,
        Maximum => Maximum,
        Automatic => Automatic,
        _ => legacyEcoMode ? Gentle : Automatic
    };

    /// <summary>
    /// Старые настройки без PerformanceMode сохраняют прежнюю семантику EcoMode/MaxParallelism.
    /// После явного выбора профиля используется безопасная CPU-зависимая величина;
    /// HDD по-прежнему ограничивается самим существующим движком.
    /// </summary>
    public static int ResolveParallelism(AppSettings settings, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int legacyOrExpert = Math.Clamp(settings.MaxParallelism, 1, 64);
        if (string.IsNullOrWhiteSpace(settings.PerformanceMode))
            return settings.EcoMode ? 1 : legacyOrExpert;

        if (settings.UseExpertParallelism)
            return legacyOrExpert;

        int cpu = Math.Max(1, processorCount);
        return Normalize(settings.PerformanceMode) switch
        {
            Gentle => 1,
            Maximum => Math.Clamp(cpu, 1, 16),
            _ => cpu == 1 ? 1 : Math.Clamp((cpu + 1) / 2, 2, 8)
        };
    }
}
