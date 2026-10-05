using Avalonia;
using Avalonia.Media;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Карточка состояния одного диска.</summary>
public sealed class DiskHealthItemViewModel
{
    public DiskHealthInfo Model { get; }
    public DiskHealthItemViewModel(DiskHealthInfo model) => Model = model;

    public string Name => Model.Name;
    public string SerialText => Model.Serial ?? "—";
    public string MediaType => Model.MediaType;
    public string BusType => Model.BusType;
    public string SizeText => Format.Bytes(Model.SizeBytes);
    public bool PredictFailure => Model.PredictFailure;

    public string HealthText => Model.Health switch
    {
        DiskHealth.Healthy => Strings.HealthHealthy,
        DiskHealth.Warning => Strings.HealthWarning,
        DiskHealth.Unhealthy => Strings.HealthUnhealthy,
        _ => Strings.HealthUnknown
    };

    // Статусные цвета берём из ресурсов темы (раньше — хардкоды светлой палитры).
    // Карточки пересоздаются при заходе на экран, поэтому смена темы подхватывается при навигации
    // (тот же компромисс, что у ResourceGeometry в ExplorerNode).
    public IBrush HealthBackground => ResourceBrush(Model.Health switch
    {
        DiskHealth.Healthy => "SuccessSoft",
        DiskHealth.Warning => "WarningSoft",
        DiskHealth.Unhealthy => "DangerSoft",
        _ => "SurfaceAlt"
    }, "#EEF1F6");

    public IBrush HealthForeground => ResourceBrush(Model.Health switch
    {
        DiskHealth.Healthy => "SuccessBrush",
        DiskHealth.Warning => "WarningBrush",
        DiskHealth.Unhealthy => "DangerBrush",
        _ => "TextSecondary"
    }, "#536074");

    private static IBrush ResourceBrush(string key, string fallback)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse(fallback));
    }

    public bool HasTemp => Model.TemperatureC is > 0;
    public string TempText => $"{Model.TemperatureC} °C";

    public bool HasPowerOn => Model.PowerOnHours is > 0;
    public string PowerOnText => $"{Model.PowerOnHours:N0} {Strings.HealthHours}";

    public bool HasWear => Model.WearPercent is int w && w > 0;
    public string WearText => $"{Model.WearPercent}%";

    public bool HasReadErrors => Model.ReadErrors is not null;
    public string ReadErrorsText => $"{Model.ReadErrors:N0}";

    public bool HasRpm => Model.SpindleRpm is > 0;
    public string RpmText => $"{Model.SpindleRpm:N0}";
}
