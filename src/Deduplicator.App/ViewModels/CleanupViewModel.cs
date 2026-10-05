using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Очистка диска от мусора (новый экран).</summary>
public partial class CleanupViewModel : PageViewModel
{
    private const int PageSize = 400;

    private readonly IJunkScanService _junk;
    private readonly IVolumeService _volumes;
    private readonly IFileOpsService _fileOps;
    private readonly List<JunkItemViewModel> _allItems = new();
    private CancellationTokenSource? _cts;
    private int _pageIndex;
    private bool _isBulkSelectionUpdate;

    public override string Title => Strings.CleanupTitle;

    public ObservableCollection<VolumeCheckItem> Roots { get; } = new();
    public ObservableCollection<JunkItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _emptyFolders = true;
    [ObservableProperty] private bool _zeroByte = true;
    [ObservableProperty] private bool _temp = true;
    [ObservableProperty] private bool _broken = true;
    [ObservableProperty] private bool _large;
    [ObservableProperty] private int _largeThresholdMb = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isScanning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _pageStatusText = "Показано 0 из 0";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _hasPreviousItems;
    [ObservableProperty] private bool _hasMoreItems;
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private string _deleteConfirmText = "";
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressDetails = "";

    /// <summary>Идёт любая длительная операция (скан или удаление) — для блокировки кнопок.</summary>
    public bool Busy => IsScanning || IsBusy;

    public CleanupViewModel(IJunkScanService junk, IVolumeService volumes, IFileOpsService fileOps)
    {
        _junk = junk;
        _volumes = volumes;
        _fileOps = fileOps;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JunkItemViewModel.IsSelected))
        {
            if (_isBulkSelectionUpdate) return;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            UpdateSelectedSummary();
        }
    }

    public override void OnNavigatedTo()
    {
        if (!Busy) _ = LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
            var connected = catalog.Where(v => v.IsConnected).ToList();
            Roots.Clear();
            foreach (var v in connected)
                Roots.Add(new VolumeCheckItem(v) { IsChecked = ShouldSelectByDefault(v) });
            if (Roots.Count > 0 && !Roots.Any(r => r.IsChecked))
            {
                foreach (var root in Roots) root.IsChecked = true;
            }
            Status = connected.Count == 0 ? Strings.ScanNoVolumes : null;
        }
        catch (Exception ex)
        {
            Status = "Не удалось прочитать список дисков. Проверьте подключение носителей и повторите.";
            Deduplicator.Core.Util.AppLog.Error("Cleanup.LoadRoots", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var roots = Roots.Where(r => r.IsChecked && r.Model.MountPath is not null)
            .Select(r => r.Model.MountPath!).ToList();
        if (roots.Count == 0) return;

        var options = new JunkScanOptions
        {
            Roots = roots,
            EmptyFolders = EmptyFolders,
            ZeroByteFiles = ZeroByte,
            TempFiles = Temp,
            BrokenShortcuts = Broken,
            LargeFiles = Large,
            LargeThresholdBytes = (long)LargeThresholdMb * 1024 * 1024
        };

        _cts = new CancellationTokenSource();
        IsScanning = true;
        Status = Strings.Loading;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressDetails = "Оцениваем объём…";
        ReplaceItems(Array.Empty<JunkItemViewModel>());
        Summary = "";

        try
        {
            var found = await Task.Run(() => _junk.Scan(options,
                progress => Dispatcher.UIThread.Post(() => ApplyProgress(progress)),
                _cts.Token));

            ReplaceItems(found.OrderByDescending(j => j.Size).Select(j => new JunkItemViewModel(j)));
            UpdateSummary();
            Status = _allItems.Count == 0 ? Strings.CleanupNothing : null;
        }
        catch (OperationCanceledException)
        {
            Status = Strings.Cancel;
        }
        catch (Exception ex)
        {
            Status = $"{Strings.CleanupError}. Данные не изменены — попробуйте ещё раз.";
            Deduplicator.Core.Util.AppLog.Error("Cleanup.Scan", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        // «Большие файлы» — это ЛИЧНЫЕ данные (видео, архивы),
        // а не мусор; в массовое выделение они не попадают — только осознанная ручная отметка.
        var largePaths = _allItems
            .Where(i => i.Model.Kind == JunkKind.LargeFile)
            .Select(i => i.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetSelection(i => i.Model.Kind != JunkKind.LargeFile && !largePaths.Contains(i.Path));
    }

    [RelayCommand]
    private void ClearSelection() => SetSelection(_ => false);

    [RelayCommand]
    private void ShowPrevious()
    {
        if (_pageIndex <= 0) return;
        _pageIndex--;
        RefreshPage();
    }

    [RelayCommand]
    private void ShowMore()
    {
        if ((_pageIndex + 1) * PageSize >= _allItems.Count) return;
        _pageIndex++;
        RefreshPage();
    }

    [ObservableProperty] private string? _selectedSummaryText;

    /// <summary>«Выбрано: N (X ГБ)» рядом с кнопкой удаления — видно масштаб до подтверждения.</summary>
    internal void UpdateSelectedSummary()
    {
        var selected = GetSelectedDeletionTargets();
        long bytes = selected.Sum(i => i.Model.Size);
        SelectedSummaryText = selected.Count == 0
            ? null
            : $"Выбрано: {selected.Count} ({Format.Bytes(bytes)})";
    }

    // #16: удаление теперь через подтверждение (как в Результатах) — один клик не сносит всё сразу.
    private bool CanDelete() => !Busy && GetSelectedDeletionTargets().Count > 0;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        int count = GetSelectedDeletionTargets().Count;
        if (count == 0) return;
        DeleteConfirmText = $"{Strings.CleanupConfirmBody}\n\n{Strings.FmSelected}: {count}";
        ShowDeleteConfirm = true;
    }

    [RelayCommand]
    private void CancelDelete() => ShowDeleteConfirm = false;

    /// <summary>Уход со страницы гасит «вооружённый» диалог — иначе он всплывает при возврате
    /// поверх обновлённого списка с устаревшим текстом.</summary>
    public override void OnNavigatedFrom() => ShowDeleteConfirm = false;

    // Единый модальный слой — затемнение сайдбара и Esc обслуживает MainWindow.
    public override bool HasOpenDialog => ShowDeleteConfirm;
    public override void CloseDialog() => CancelDelete();
    partial void OnShowDeleteConfirmChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        ShowDeleteConfirm = false;
        var selected = GetSelectedDeletionTargets();
        if (selected.Count == 0) return;

        IsBusy = true;
        ProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressDetails = $"Обработано 0 из {selected.Count:N0}";
        try
        {
            // #15: учитываем результат — из списка убираем только реально удалённое, об остальном сообщаем.
            var (failed, allGone) = await Task.Run(() =>
            {
                var bad = new System.Collections.Generic.List<JunkItemViewModel>();
                var started = System.Diagnostics.Stopwatch.StartNew();
                for (int index = 0; index < selected.Count; index++)
                {
                    var i = selected[index];
                    // Страховка: исключение по одному элементу (сбой журнала/носителя) не валит пакет.
                    bool ok;
                    try { ok = _fileOps.RecyclePath(i.Path); }
                    catch { ok = false; }
                    if (!ok) bad.Add(i);
                    int completed = index + 1;
                    if (completed == selected.Count || completed % 10 == 0)
                    {
                        double fraction = completed / (double)selected.Count;
                        double remainingSeconds = completed > 0
                            ? started.Elapsed.TotalSeconds * (selected.Count - completed) / completed : 0;
                        Dispatcher.UIThread.Post(() =>
                        {
                            ProgressValue = fraction * 100;
                            ProgressDetails = $"Обработано {completed:N0} из {selected.Count:N0}" +
                                              (remainingSeconds >= 1
                                                  ? $" · осталось примерно {FormatDuration(TimeSpan.FromSeconds(remainingSeconds))}"
                                                  : "");
                        });
                    }
                }
                // Все неудачные пути уже не существуют → скорее всего диск выдернули,
                // а не «не удалилось» — подсказываем это простыми словами.
                bool gone = bad.Count > 0 && bad.All(i =>
                {
                    try { return !File.Exists(i.Path) && !Directory.Exists(i.Path); }
                    catch { return false; }
                });
                return (bad, gone);
            });

            var failedSet = failed.ToHashSet();
            var removed = selected.Where(i => !failedSet.Contains(i)).ToHashSet();
            foreach (var i in removed) i.PropertyChanged -= OnItemPropertyChanged;
            _allItems.RemoveAll(i => removed.Contains(i));
            RefreshPage();

            UpdateSummary();
            UpdateSelectedSummary();
            Status = failed.Count == 0
                ? $"Готово: удалено {removed.Count:N0} · освобождено {Format.Bytes(removed.Sum(i => i.Model.Size))}. История доступна в Журнале."
                : allGone
                    ? $"Файлы недоступны — возможно, диск отключён (ничего не удалено): {failed.Count}"
                    : $"{Strings.CleanupDeleteFailed}: {failed.Count}";
        }
        finally
        {
            // #14: даже при исключении кнопки не остаются заблокированными навсегда.
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        long bytes = _allItems.Sum(i => i.Model.Size);
        Summary = $"{Strings.CleanupFound}: {_allItems.Count} · {Format.Bytes(bytes)}";
    }

    private void ApplyProgress(JunkScanProgress progress)
    {
        ProgressIndeterminate = progress.Fraction is null;
        ProgressValue = (progress.Fraction ?? 0) * 100;
        Status = $"Проверено файлов: {progress.FilesVisited:N0} · найдено: {progress.ItemsFound:N0}";
        string elapsed = FormatDuration(progress.Elapsed);
        string remaining = progress.EstimatedRemaining is { } eta
            ? $" · осталось примерно {FormatDuration(eta)}"
            : progress.Fraction is < 1 ? " · считаем оставшееся время…" : "";
        ProgressDetails = progress.Fraction is { } fraction
            ? $"{fraction:P0} · прошло {elapsed}{remaining}"
            : $"Прошло {elapsed}{remaining}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} ч {value.Minutes:00} мин";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes} мин {value.Seconds:00} с";
        return $"{Math.Max(0, value.Seconds)} с";
    }

    /// <summary>
    /// Возвращает уникальные физические пути, которые действительно разрешено удалить.
    /// Защита остаётся и для сторонней реализации IJunkScanService: если один путь ошибочно
    /// пришёл как обычный мусор и как большой файл, удалить его можно только явной отметкой
    /// именно записи большого файла.
    /// </summary>
    private List<JunkItemViewModel> GetSelectedDeletionTargets()
    {
        var largeByPath = new Dictionary<string, JunkItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems)
        {
            if (item.Model.Kind == JunkKind.LargeFile)
                largeByPath.TryAdd(item.Path, item);
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<JunkItemViewModel>();
        foreach (var selected in _allItems.Where(i => i.IsSelected))
        {
            var candidate = selected;
            if (largeByPath.TryGetValue(selected.Path, out var large))
            {
                if (!large.IsSelected) continue;
                candidate = large;
            }

            if (uniquePaths.Add(candidate.Path)) result.Add(candidate);
        }

        return result;
    }

    private void SetSelection(Func<JunkItemViewModel, bool> selector)
    {
        _isBulkSelectionUpdate = true;
        try
        {
            foreach (var item in _allItems) item.IsSelected = selector(item);
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }

        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UpdateSelectedSummary();
    }

    private void ReplaceItems(IEnumerable<JunkItemViewModel> items)
    {
        foreach (var item in _allItems) item.PropertyChanged -= OnItemPropertyChanged;
        _allItems.Clear();

        foreach (var item in items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            _allItems.Add(item);
        }

        _pageIndex = 0;
        RefreshPage();
        UpdateSelectedSummary();
    }

    /// <summary>
    /// В визуальном дереве одновременно находится не больше одной страницы. Полный набор и его
    /// состояние выбора остаются в <see cref="_allItems"/>, поэтому переходы между страницами
    /// не теряют отметки, а массовые операции применяются ко всем найденным элементам.
    /// </summary>
    private void RefreshPage()
    {
        int total = _allItems.Count;
        int maxPage = total == 0 ? 0 : (total - 1) / PageSize;
        _pageIndex = Math.Clamp(_pageIndex, 0, maxPage);

        int start = _pageIndex * PageSize;
        int end = Math.Min(start + PageSize, total);

        Items.Clear();
        for (int index = start; index < end; index++) Items.Add(_allItems[index]);

        HasResults = total > 0;
        HasPreviousItems = start > 0;
        HasMoreItems = end < total;
        PageStatusText = total == 0
            ? "Показано 0 из 0"
            : $"Показано {end} из {total} · на экране {start + 1}–{end}";

        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private static bool ShouldSelectByDefault(VolumeRecord volume)
    {
        if (string.IsNullOrWhiteSpace(volume.MountPath)) return true;

        try
        {
            string? systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            string? mountRoot = Path.GetPathRoot(volume.MountPath);
            if (string.IsNullOrWhiteSpace(systemRoot) || string.IsNullOrWhiteSpace(mountRoot)) return true;

            return !string.Equals(systemRoot, mountRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
