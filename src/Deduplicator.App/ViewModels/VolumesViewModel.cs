using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Главный экран — каталог носителей и сводка по базе (экран 1).</summary>
public partial class VolumesViewModel : PageViewModel
{
    private readonly IVolumeService _volumes;
    private readonly VolumeRepository _volumeRepo;
    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly IDriveWatcher _watcher;
    private readonly NavigationService _nav;
    private bool _loading;

    public override string Title => Strings.VolumesTitle;

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _totalVolumes;
    [ObservableProperty] private string _totalFilesText = "0";
    [ObservableProperty] private string _totalSizeText = "—";
    [ObservableProperty] private string? _error;

    [ObservableProperty] private bool _showRemoveConfirm;
    private VolumeItemViewModel? _pendingRemove;

    public VolumesViewModel(IVolumeService volumes, VolumeRepository volumeRepo, FileRepository files,
        GroupRepository groups, IDriveWatcher watcher, NavigationService nav)
    {
        _volumes = volumes;
        _volumeRepo = volumeRepo;
        _files = files;
        _groups = groups;
        _watcher = watcher;
        _nav = nav;
        // Живое обновление каталога при подключении/извлечении носителя.
        _watcher.DrivesChanged += () => Dispatcher.UIThread.Post(() => { if (!_loading) _ = RefreshAsync(); });
    }

    public override void OnNavigatedTo() => _ = RefreshAsync();

    /// <summary>Уход со страницы сбрасывает незавершённый диалог удаления — иначе он «всплывёт» при возврате.</summary>
    public override void OnNavigatedFrom()
    {
        ShowRemoveConfirm = false;
        _pendingRemove = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        IsBusy = true;
        Error = null;
        try
        {
            var (items, total) = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog();
                var (files, bytes) = _files.TotalIndexed();
                return (catalog.Select(v => new VolumeItemViewModel(v)).ToList(), (files, bytes));
            });

            Volumes.Clear();
            foreach (var i in items) Volumes.Add(i);
            TotalVolumes = items.Count;
            TotalFilesText = $"{total.files:N0}";
            TotalSizeText = Format.Bytes(total.bytes);
        }
        catch (Exception ex)
        {
            Error = "Не удалось обновить список дисков. Проверьте подключение носителей и повторите.";
            Core.Util.AppLog.Error("Volumes.Refresh", ex);
        }
        finally
        {
            IsBusy = false;
            _loading = false;
        }
    }

    [RelayCommand]
    private void Remove(VolumeItemViewModel? item)
    {
        if (item is null) return;
        _pendingRemove = item;
        ShowRemoveConfirm = true;
    }

    [RelayCommand]
    private void CancelRemove() { ShowRemoveConfirm = false; _pendingRemove = null; }

    // Единый модальный слой — затемнение сайдбара и Esc обслуживает MainWindow.
    public override bool HasOpenDialog => ShowRemoveConfirm;
    public override void CloseDialog() => CancelRemove();
    partial void OnShowRemoveConfirmChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        ShowRemoveConfirm = false;
        var item = _pendingRemove;
        _pendingRemove = null;
        if (item is null) return;
        try
        {
            await Task.Run(() =>
            {
                _volumeRepo.Delete(item.Id);
                // FK-каскад снёс files/duplicate_members этого тома, но пустые/одиночные группы и
                // осиротевшие эталоны остались бы в результатах и статистике.
                try { _groups.DissolveDegenerateGroups(); }
                catch (Exception ex) { Core.Util.AppLog.Error("DissolveDegenerateGroups", ex); }
            });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Error = "Не удалось убрать носитель из каталога. Данные не изменены — попробуйте ещё раз.";
            Core.Util.AppLog.Error("Volumes.Remove", ex);
        }
    }

    [RelayCommand]
    private void Scan(VolumeItemViewModel? item)
    {
        if (item is null) return;
        _nav.Get<ScanViewModel>().Preselect(item.Id);
        _nav.To<ScanViewModel>();
    }

    [RelayCommand]
    private void OpenInExplorer(VolumeItemViewModel? item)
    {
        if (item?.MountPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.MountPath,
                UseShellExecute = true
            });
        }
        catch { /* нет прав/носитель занят — игнорируем */ }
    }

    [RelayCommand]
    private void Explore(VolumeItemViewModel? item)
    {
        if (item is null) return;
        _nav.Get<ExplorerViewModel>().Select(item.Id);
        _nav.To<ExplorerViewModel>();
    }
}
