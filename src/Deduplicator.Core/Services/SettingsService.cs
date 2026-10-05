using System.Text.Json;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event Action<AppSettings>? Changed;
}

/// <summary>Загрузка/сохранение настроек в JSON (полностью офлайн).</summary>
public sealed class SettingsService : ISettingsService
{
    private const int MaxSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public SettingsService(AppPaths paths)
    {
        _path = paths.SettingsPath;
        Current = Load();
        // Карантин по умолчанию — из AppPaths, если не задан явно.
        Current.QuarantinePath ??= paths.QuarantineDir;

        // Одноразовая миграция авто-обновлений: у настроек, сохранённых до появления функции,
        // маркер UpdatesConfigured отсутствует (false) — включаем авто-проверку и фиксируем маркер,
        // чтобы дальше уважать ручной выбор. Сразу сохраняем, чтобы не мигрировать при каждом запуске.
        if (!Current.UpdatesConfigured)
        {
            Current.CheckUpdatesEnabled = true;
            Current.UpdatesConfigured = true;
            try { Save(Current); } catch { /* не критично — применится при следующем сохранении */ }
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = SafeIo.ReadAllTextLimited(_path, MaxSettingsBytes);
                using var document = JsonDocument.Parse(json);
                bool hasPerformanceMode = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.EnumerateObject().Any(p =>
                        string.Equals(p.Name, nameof(AppSettings.PerformanceMode), StringComparison.OrdinalIgnoreCase));
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    // Старый settings.json не знал профилей. Null включает точную legacy-семантику
                    // EcoMode/MaxParallelism вместо незаметной смены производительности после обновления.
                    if (!hasPerformanceMode) loaded.PerformanceMode = null;
                    // Валидация диапазонов на случай ручной правки settings.json.
                    loaded.DefaultImageThreshold = Math.Clamp(loaded.DefaultImageThreshold, 0.5, 1.0);
                    loaded.DefaultAudioThreshold = Math.Clamp(loaded.DefaultAudioThreshold, 0.5, 1.0);
                    if (loaded.MaxParallelism < 1) loaded.MaxParallelism = 1;
                    if (loaded.MaxParallelism > 64) loaded.MaxParallelism = 64;
                    loaded.UiScale = Math.Clamp(loaded.UiScale, 1.0, 1.5);
                    if (!string.IsNullOrWhiteSpace(loaded.PerformanceMode))
                        loaded.PerformanceMode = PerformanceProfiles.Normalize(loaded.PerformanceMode);
                    return loaded;
                }
            }
        }
        catch { /* повреждённые настройки — используем значения по умолчанию */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            SafeIo.WriteAllTextAtomic(_path, json);
        }
        catch { /* не критично для работы */ }
        Changed?.Invoke(settings);
    }
}
