using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Носитель с флажком выбора для сверки/консолидации.</summary>
public partial class VolumeCheckItem : ObservableObject
{
    public VolumeRecord Model { get; }
    public VolumeCheckItem(VolumeRecord model) => Model = model;

    public long Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string? MountPath => Model.MountPath;
    public bool IsConnected => Model.IsConnected;
    public string StatusText => Model.IsConnected ? Strings.Connected : Strings.Disconnected;
    public string IndexedText => Format.Count(Model.IndexedFileCount, "файл", "файла", "файлов");
    public bool HasScanFolder => !string.IsNullOrWhiteSpace(ScanFolder);
    public string PickFolderText => HasScanFolder ? "Сменить папку" : "Выбрать папку";
    public string ScanScopeText => HasScanFolder
        ? $"Будет проверена только папка: {ScanFolder}"
        : "Будет проверен весь выбранный диск";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _scanFolder;

    partial void OnScanFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScanFolder));
        OnPropertyChanged(nameof(PickFolderText));
        OnPropertyChanged(nameof(ScanScopeText));
    }
}
