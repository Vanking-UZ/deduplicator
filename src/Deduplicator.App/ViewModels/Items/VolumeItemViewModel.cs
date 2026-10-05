using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Элемент списка носителей.</summary>
public sealed class VolumeItemViewModel : ObservableObject
{
    public VolumeRecord Model { get; }
    public VolumeItemViewModel(VolumeRecord model) => Model = model;

    public long Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string Serial => Model.Serial ?? "—";
    public string Fs => Model.FsType ?? "—";
    public bool IsConnected => Model.IsConnected;
    public string? MountPath => Model.MountPath;
    public string CapacityText => Format.Bytes(Model.TotalBytes);
    public string IndexedText => Format.Count(Model.IndexedFileCount, "файл", "файла", "файлов");

    public string LastScanText => Model.LastScanned is { } d
        ? $"{Strings.LastScan}: {Format.DateShort(d)}"
        : Strings.NeverScanned;

    /// <summary>Сводка по индексации: количество файлов показываем только после сканирования.</summary>
    public string SummaryLine => Model.IndexedFileCount > 0
        ? $"{Format.Count(Model.IndexedFileCount, "файл", "файла", "файлов")} запомнено   ·   {LastScanText}"
        : LastScanText;

    public string StatusText => IsConnected ? Strings.Connected : Strings.Disconnected;
}
