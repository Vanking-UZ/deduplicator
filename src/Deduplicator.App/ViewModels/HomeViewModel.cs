using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record HomeDiskItem(
    string Name,
    string StatusText,
    string FilesText,
    string LastScanText,
    string CapacityText,
    bool IsConnected);

/// <summary>Простой стартовый экран для основных действий без технической навигации.</summary>
public partial class HomeViewModel : PageViewModel
{
    private readonly NavigationService _nav;
    private readonly IVolumeService _volumes;
    private readonly IStatisticsService _statistics;
    private readonly Deduplicator.Core.Data.OperationLogRepository _opLog;

    // Подсказка «носитель с фотографиями»: снапшот подключённых корней (чтобы отличать НОВЫЙ том),
    // серийники, о которых уже предлагали (не дребезжим в течение сессии), и том для предвыбора.
    // _photoHintGate: DrivesChanged приходит с таймера вотчера — медленный SyncAndGetCatalog может
    // пережить следующий тик, не даём обработчикам гоняться за общими множествами.
    private readonly object _photoHintGate = new();
    private HashSet<string> _driveRootsSnapshot;
    private readonly HashSet<string> _offeredPhotoSerials = new(System.StringComparer.OrdinalIgnoreCase);
    private long? _photoHintVolumeId;

    public override string Title => "Главная";

    public ObservableCollection<HomeDiskItem> RecentDisks { get; } = new();

    /// <summary>Подключён носитель с папкой DCIM (метка) — App показывает тост. UI-поток.</summary>
    public event Action<string>? PhotoDeviceDetected;

    /// <summary>Текст баннера «носитель с фотографиями»; пустая строка — баннер скрыт.</summary>
    [ObservableProperty] private string _photoDeviceHintText = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusTitle = "Готово к проверке";
    [ObservableProperty] private string _statusText = "Подключите диск и запустите поиск дубликатов.";
    [ObservableProperty] private string _safetyText = "Безопасно: удаление только после подтверждения.";
    [ObservableProperty] private string _connectedDisksText = "0";
    [ObservableProperty] private string _catalogDisksText = "0";
    [ObservableProperty] private string _indexedFilesText = "0";
    [ObservableProperty] private string _duplicateGroupsText = "0";
    [ObservableProperty] private string _reclaimableText = "0 Б";
    [ObservableProperty] private string _lastSnapshotText = "Проверок пока не было";
    [ObservableProperty] private string _freedTotalText = "";
    [ObservableProperty] private string _noDisksText = "";
    [ObservableProperty] private bool _hasDuplicateResults;
    // Есть что освобождать (сверенные точные копии с ненулевым выигрышем места). Отделено от
    // HasDuplicateResults: сотни «похожих»/непроверенных групп дают 0 Б к освобождению — показывать
    // на главной «Можно освободить 0 Б» было бы бессмысленно и тревожно (правка по скрину пользователя).
    [ObservableProperty] private bool _hasReclaimable;

    public HomeViewModel(NavigationService nav, IVolumeService volumes, IStatisticsService statistics,
        Deduplicator.Core.Data.OperationLogRepository opLog, IDriveWatcher driveWatcher)
    {
        _nav = nav;
        _volumes = volumes;
        _statistics = statistics;
        _opLog = opLog;
        // Подсказка «флешка с фотографиями»: ловим ПОЯВЛЕНИЕ нового тома с папкой DCIM.
        _driveRootsSnapshot = GetReadyRoots();
        driveWatcher.DrivesChanged += OnDrivesChangedForPhotoHint;
    }

    public override void OnNavigatedTo() => _ = RefreshAsync();

    // ---- Подсказка «носитель с фотографиями» (сторожевые функции, задача B) ----

    /// <summary>Корни готовых локальных/съёмных дисков. Сетевые не проверяем: Directory.Exists
    /// по полумёртвой шаре виснет, а «флешка с фото» сетевой не бывает.</summary>
    private static HashSet<string> GetReadyRoots()
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                        set.Add(d.RootDirectory.FullName);
                }
                catch { /* носитель извлекли между перечислением и опросом */ }
            }
        }
        catch { /* перечисление недоступно — подсказка просто промолчит */ }
        return set;
    }

    /// <summary>Вызывается вотчером в фоновом потоке: дёшево ищем НОВЫЙ том с папкой \DCIM.</summary>
    private void OnDrivesChangedForPhotoHint()
    {
        try
        {
            lock (_photoHintGate)
            {
                var current = GetReadyRoots();
                var added = current.Where(r => !_driveRootsSnapshot.Contains(r)).ToList();
                _driveRootsSnapshot = current;

                foreach (var root in added)
                {
                    if (!Directory.Exists(Path.Combine(root, "DCIM"))) continue;

                    // Том нужен из каталога — его Id идёт в предвыбор на экране сверки.
                    var vol = _volumes.SyncAndGetCatalog().FirstOrDefault(v =>
                        v.IsConnected && string.Equals(v.MountPath, root, System.StringComparison.OrdinalIgnoreCase));
                    if (vol is null) continue;

                    // Дребезг: тот же носитель (серийник) в течение сессии повторно не предлагаем.
                    if (!_offeredPhotoSerials.Add(vol.Serial ?? root)) continue;

                    var label = vol.DisplayName;
                    var volumeId = vol.Id;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _photoHintVolumeId = volumeId;
                        PhotoDeviceHintText = $"Подключён диск с фотографиями ({label}). Проверить на дубликаты?";
                        try { PhotoDeviceDetected?.Invoke(label); } catch { }
                    });
                    break; // одной подсказки за раз достаточно
                }
            }
        }
        catch (Exception ex)
        {
            // Подсказка — вспомогательная: любой сбой глотаем, приложение не трогаем.
            Core.Util.AppLog.Error("PhotoDeviceHint", ex);
        }
    }

    /// <summary>Кнопка «Проверить» баннера/клик по тосту: экран «Найти дубликаты» с предвыбранным
    /// томом и глубиной «похожие фото».</summary>
    [RelayCommand]
    private void CheckPhotoDevice()
    {
        if (_photoHintVolumeId is not long id) return;
        var scan = _nav.Get<ScanViewModel>();
        scan.Preselect(id);
        scan.PreselectDepth(ScanDepth.WithSimilarImages);
        PhotoDeviceHintText = "";
        _photoHintVolumeId = null;
        _nav.To<ScanViewModel>();
    }

    [RelayCommand]
    private void DismissPhotoHint()
    {
        PhotoDeviceHintText = "";
        _photoHintVolumeId = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var summary = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog();
                var stats = _statistics.Compute();
                long freed = 0;
                try { freed = _opLog.GetTotalFreedBytes(); } catch { }
                return new
                {
                    Freed = freed,
                    Catalog = catalog,
                    Stats = stats,
                    Connected = catalog.Count(v => v.IsConnected),
                    LastScan = catalog
                        .Where(v => v.LastScanned is not null)
                        .Select(v => v.LastScanned!.Value)
                        .DefaultIfEmpty()
                        .Max()
                };
            });

            ConnectedDisksText = $"{summary.Connected:N0}";
            CatalogDisksText = $"{summary.Catalog.Count:N0}";
            IndexedFilesText = $"{summary.Stats.TotalIndexedFiles:N0}";
            DuplicateGroupsText = $"{summary.Stats.TotalGroups:N0}";
            ReclaimableText = Format.Bytes(summary.Stats.TotalReclaimableBytes);
            HasDuplicateResults = summary.Stats.TotalGroups > 0;
            HasReclaimable = summary.Stats.TotalReclaimableBytes > 0;
            LastSnapshotText = summary.LastScan == default
                ? "Проверок пока не было"
                : $"Последняя проверка: {Format.DateShort(summary.LastScan)}";
            // 1.10: «до/после» — сколько места программа реально освободила за всё время.
            FreedTotalText = summary.Freed > 0
                ? $"Освобождено с помощью программы: {Format.Bytes(summary.Freed)}"
                : "";

            StatusTitle = BuildStatusTitle(summary.Connected, summary.Stats);
            StatusText = BuildStatusText(summary.Connected, summary.Stats);

            RecentDisks.Clear();
            foreach (var disk in summary.Catalog
                         .OrderByDescending(v => v.IsConnected)
                         .ThenByDescending(v => v.LastScanned ?? v.LastSeen)
                         .Take(5))
            {
                RecentDisks.Add(new HomeDiskItem(
                    disk.DisplayName,
                    disk.IsConnected ? "Подключён" : "Сохранён в архиве",
                    Format.Count(disk.IndexedFileCount, "файл", "файла", "файлов"),
                    disk.LastScanned is null ? "Ещё не проверялся" : Format.DateShort(disk.LastScanned.Value),
                    Format.Bytes(disk.TotalBytes),
                    disk.IsConnected));
            }

            NoDisksText = RecentDisks.Count == 0
                ? "Подключите первый диск, чтобы начать."
                : "";
        }
        catch (Exception ex)
        {
            StatusTitle = "Не удалось обновить сводку";
            StatusText = "Данные не изменены. Проверьте подключение дисков и попробуйте ещё раз.";
            Core.Util.AppLog.Error("Home.Refresh", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDuplicateSearch() => _nav.To<ScanViewModel>();

    [RelayCommand]
    private void OpenDuplicateResults() => _nav.To<ResultsViewModel>();

    [RelayCommand]
    private void OpenCleanup() => _nav.To<CleanupViewModel>();

    [RelayCommand]
    private void OpenDiskHealth() => _nav.To<DiskHealthViewModel>();

    [RelayCommand]
    private void OpenDiskArchive() => _nav.To<VolumesViewModel>();

    [RelayCommand]
    private void OpenSavedFiles() => _nav.To<ExplorerViewModel>();

    [RelayCommand]
    private void OpenCrossDisk() => _nav.To<CrossDiskViewModel>();

    [RelayCommand]
    private void OpenCleanArchive() => _nav.To<ConsolidationViewModel>();

    [RelayCommand]
    private void OpenStatistics() => _nav.To<StatisticsViewModel>();

    [RelayCommand]
    private void OpenJournal() => _nav.To<JournalViewModel>();

    private static string BuildStatusTitle(int connected, DuplicateStatistics stats)
    {
        if (stats.TotalReclaimableBytes > 0) return "Можно освободить место";
        if (stats.TotalGroups > 0) return "Проверка завершена";
        if (connected == 0 && stats.TotalIndexedFiles > 0) return "Последняя проверка сохранена";
        if (connected == 0) return "Подключите диск или выберите папку";
        if (stats.TotalIndexedFiles > 0) return "Всё готово к новой проверке";
        return "Начните с простой проверки";
    }

    private static string BuildStatusText(int connected, DuplicateStatistics stats)
    {
        if (stats.TotalReclaimableBytes > 0)
            return $"Найдено {stats.TotalGroups:N0} групп копий. Просмотрите предложения — без вашего подтверждения ничего не удалится.";

        if (stats.TotalGroups > 0)
            return $"Найдено {stats.TotalGroups:N0} групп похожих файлов. Точных копий для освобождения места не нашлось — откройте результаты и решите сами.";

        if (connected == 0 && stats.TotalIndexedFiles > 0)
            return "Списки файлов доступны даже без подключённых дисков. Подключите носитель для новой проверки или откройте сохранённые данные.";

        if (connected == 0)
            return "Подключите внешний диск, флешку или выберите папку. Рекомендуемые безопасные настройки уже включены.";

        if (stats.TotalIndexedFiles > 0)
            return "Подключённые диски видны программе. Выберите, где искать, — остальные параметры можно не менять.";

        return "Выберите диск или папку. Программа найдёт одинаковые файлы и сначала покажет их вам для проверки.";
    }
}
