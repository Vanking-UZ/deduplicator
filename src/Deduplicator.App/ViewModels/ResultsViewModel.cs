using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.App.ViewModels;

/// <summary>Результаты: точные дубликаты и похожие (экраны 5–6).</summary>
public partial class ResultsViewModel : PageViewModel
{
    private readonly GroupRepository _groups;
    private readonly IVolumeService _volumes;
    private readonly IImageMatchService _thumbs;
    private readonly IFileOpsService _fileOps;
    private readonly IHashService _hash;
    private readonly ISettingsService _settings;
    private readonly IBatchRenameService _rename;
    private readonly IResultDistributionService _distribute;
    private readonly FileRepository _files;
    private readonly AppPaths _paths;
    private readonly ScanSessionRepository _sessions;
    private readonly NavigationService _nav;
    private readonly IScanService _scan;

    public override string Title => Strings.ResultsTitle;

    public ObservableCollection<GroupItemViewModel> ExactGroups { get; } = new();
    public ObservableCollection<GroupItemViewModel> SimilarImageGroups { get; } = new();
    public ObservableCollection<GroupItemViewModel> SimilarAudioGroups { get; } = new();

    // Загруженные (построенные) VM похожих групп — накапливаются ПОСТРАНИЧНО (только открытые страницы),
    // миниатюры декодируются лишь для них. Слайдер порога фильтрует этот загруженный набор.
    private readonly List<GroupItemViewModel> _allImage = new();
    private readonly List<GroupItemViewModel> _allAudio = new();
    // Похожие вкладки строим ЛЕНИВО (по открытию вкладки) и ПОСТРАНИЧНО из БД, а не всё в память: иначе
    // сразу декодируются сотни миниатюр (наблюдалось ~1.44 ГБ) и материализуются все группы (P1).
    // P1.1: точная вкладка тоже грузится постранично из БД.
    private int _exactTotal, _exactOffset;
    private int _imageTotal, _imageOffset;   // всего похожих фото в сессии / сколько уже загружено страницами
    private int _audioTotal, _audioOffset;
    private long? _sessionFilter;
    private static readonly MatchType[] ExactTypes = { MatchType.Exact, MatchType.ExactVideo };
    private Dictionary<long, string> _mountById = new();
    private Dictionary<long, string> _labelById = new();
    private bool _imageBuilt, _audioBuilt;
    private string[] _referenceFolders = Array.Empty<string>();
    private bool _isLoading;
    private bool _loadErrorVisible;

    /// <summary>Сколько групп показываем за раз (первая страница + «Показать ещё») — ТЗ MVP.</summary>
    private const int PageSize = 100;
    [ObservableProperty] private bool _canShowMoreExact;
    [ObservableProperty] private bool _canShowMoreSimilarImages;
    [ObservableProperty] private bool _canShowMoreSimilarAudio;
    // Текст индикатора загрузки (по умолчанию «Загрузка…»; на ревалидации удаления — отдельное сообщение).
    [ObservableProperty] private string _busyStatus = Strings.Loading;
    [ObservableProperty] private string? _busySubText;
    private CancellationTokenSource? _busyOperationCts;
    private Stopwatch? _busyOperationStopwatch;

    /// <summary>Устанавливается представлением — выбор файла для сохранения отчёта.</summary>
    public Func<string, Task<string?>>? PickSaveFile { get; set; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _imageThreshold = 0.90;
    [ObservableProperty] private double _audioThreshold = 0.85;
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private string _deleteConfirmText = "";
    [ObservableProperty] private string? _exactEmpty;
    [ObservableProperty] private string? _imageEmpty;
    [ObservableProperty] private string? _audioEmpty;
    [ObservableProperty] private string _exactCountText = "";
    [ObservableProperty] private string _imageCountText = "";
    [ObservableProperty] private string _audioCountText = "";

    // P0-B: групповое переименование выбранных файлов.
    [ObservableProperty] private bool _showRenameDialog;
    [ObservableProperty] private string _renameBaseName = "";
    [ObservableProperty] private int _renameStartNumber = 1;
    [ObservableProperty] private string _renamePreviewText = "";
    [ObservableProperty] private string? _renameConflictText;
    [ObservableProperty] private bool _renameCanApply;
    private List<RenameSourceFile> _renameSources = new();
    private Dictionary<long, (long volumeId, string mount)> _renameMeta = new();

    // P0.4: видимый баннер результата операции (rename/delete) — НЕ прячется вместе с диалогом.
    [ObservableProperty] private string? _operationErrorText;
    [ObservableProperty] private string? _operationSuccessText;

    // Постоянная сводка «Отмечено: N файлов · X ГБ» + снятие отметок.
    [ObservableProperty] private string? _markedSummaryText;

    // 2.8: предпросмотр идущего скана — точные группы АКТИВНОЙ сессии видны до завершения.
    // В этом режиме все действия над файлами (удаление/переименование/распределение/ссылки/
    // авто-выбор) отключены; галочки безвредны и продолжают работать.
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private string? _previewBannerText;
    private bool _isActive;            // страница сейчас открыта (OnNavigatedTo/OnNavigatedFrom)
    private bool _previewLoaded;       // предпросмотр уже показан — не перезагружаться на каждый тик
    private bool _terminalReloadQueued; // терминальная перезагрузка уже запланирована

    private const string PreviewBannerMessage =
        "Проверка ещё идёт — это предварительные результаты. Удаление станет доступно после завершения.";
    private const string PreviewSimilarPendingMessage = "Похожие появятся после завершения проверки.";
    private const string PreviewActionBlockedMessage =
        "Проверка ещё идёт — действия с файлами станут доступны после её завершения.";

    // «Освободить N ГБ» (этап 1.9): жадный подбор проверенных групп под целевой объём.
    [ObservableProperty] private string _freeUpGbText = "5";

    public string ManualHint => Strings.SimilarManualHint;
    public bool IsExactTab => SelectedTab == 0;
    public bool IsSimilarTab => SelectedTab != 0;
    public string SelectionSafetyText => IsExactTab
        ? "В каждой группе останется минимум одна копия"
        : "Это похожие, а не одинаковые файлы — проверяйте вручную";

    public ResultsViewModel(GroupRepository groups, IVolumeService volumes, IImageMatchService thumbs,
        IFileOpsService fileOps, IHashService hash, ISettingsService settings, IBatchRenameService rename,
        FileRepository files, AppPaths paths, ScanSessionRepository sessions, IResultDistributionService distribute,
        NavigationService nav, IScanService scan)
    {
        _nav = nav;
        _groups = groups;
        _volumes = volumes;
        _thumbs = thumbs;
        // The comparison overlay is created even while hidden. Non-null inert items keep
        // its deferred bindings quiet until a real pair is selected.
        _viewerLeft = CreateViewerPlaceholder();
        _viewerRight = CreateViewerPlaceholder();
        _fileOps = fileOps;
        _hash = hash;
        _settings = settings;
        _rename = rename;
        _distribute = distribute;
        _files = files;
        _paths = paths;
        _sessions = sessions;
        _scan = scan;
        _imageThreshold = settings.Current.DefaultImageThreshold;
        _audioThreshold = settings.Current.DefaultAudioThreshold;
        // 2.8: оба сервиса — синглтоны, подписка живёт всё время работы приложения (не течёт).
        _scan.ProgressChanged += OnScanProgress;
    }

    /// <summary>
    /// 2.8: следим за идущим сканом. Перезагружаемся ТОЛЬКО дважды: на первом «точные готовы»
    /// (появился предпросмотр) и на терминальном переходе (Completed/Cancelled/Failed) — а не на
    /// каждом тике прогресса, иначе UI дёргался бы. Паттерн диспатчера — как в ScanViewModel.OnProgress.
    /// </summary>
    private void OnScanProgress(ScanProgress p) => Dispatcher.UIThread.Post(() =>
    {
        if (p.Stage is ScanStage.Completed or ScanStage.Cancelled or ScanStage.Failed)
        {
            _previewLoaded = false;
            if (_terminalReloadQueued) return;
            _terminalReloadQueued = true;
            _ = ReloadAfterScanEndAsync();
            return;
        }
        // Первый ActiveExactReady: если страница открыта — показать предпросмотр сразу.
        if (!_previewLoaded && _scan.IsRunning && _scan.ActiveExactReady && _scan.ActiveSessionId is not null)
        {
            _previewLoaded = true;
            if (_isActive) _ = LoadAsync();
        }
    });

    /// <summary>
    /// Терминальное событие прилетает ДО того, как RunCore закроет сессию и подчистит группы
    /// (finally). Дожидаемся фактического завершения (IsRunning=false) и лишь потом перечитываем:
    /// предпросмотр сменится финальными результатами или откатится к последней завершённой сессии.
    /// </summary>
    private async Task ReloadAfterScanEndAsync()
    {
        try
        {
            for (int i = 0; i < 200 && _scan.IsRunning; i++)
                await Task.Delay(50);
        }
        finally { _terminalReloadQueued = false; }
        if (_isActive) await LoadAsync();
    }

    public override void OnNavigatedTo() { _isActive = true; _ = LoadAsync(); }

    /// <summary>
    /// Уход со страницы: сбрасываем «вооружённые» диалоги. Иначе при возврате поверх свежих
    /// (перезагруженных, без отметок) данных всплывал устаревший диалог, а сохранённый план
    /// переименования выполнялся по уже невидимой пользователю выборке.
    /// </summary>
    public override void OnNavigatedFrom()
    {
        _isActive = false;
        _busyOperationCts?.Cancel();
        ShowDeleteConfirm = false;
        ShowRenameDialog = false;
        ShowDistributeDialog = false;
        ShowHardlinkConfirm = false;
        _renamePlanField = null;
        _hardlinkGroup = null;
        CloseViewer(); // просмотрщик держит полноразмерные Bitmap-ы — освобождаем при уходе
    }

    // --- Единый модальный слой (затемнение сайдбара + Esc в MainWindow) ---
    /// <summary>Просмотрщик тоже участвует: его Esc обрабатывается глобально (локальный хендлер
    /// вьюхи оставляет себе только стрелки ←/→).</summary>
    public override bool HasOpenDialog =>
        IsBusy || ShowDeleteConfirm || ShowRenameDialog || ShowDistributeDialog || ShowHardlinkConfirm || ShowViewer;

    /// <summary>Esc закрывает верхний диалог. Одновременно открыт максимум один (кнопки других
    /// недоступны под оверлеем), порядок веток — на случай гонки состояний.</summary>
    public override void CloseDialog()
    {
        if (IsBusy && CanCancelBusy()) CancelBusy();
        else if (ShowDeleteConfirm) CancelDelete();
        else if (ShowRenameDialog) CancelRename();
        else if (ShowDistributeDialog) CancelDistribute();
        else if (ShowHardlinkConfirm) CancelHardlink();
        else if (ShowViewer) CloseViewer();
    }

    partial void OnShowDeleteConfirmChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));
    partial void OnShowRenameDialogChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));
    partial void OnShowDistributeDialogChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));
    partial void OnShowHardlinkConfirmChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));
    partial void OnShowViewerChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasOpenDialog));

    private bool CanCancelBusy() => _busyOperationCts is { IsCancellationRequested: false };
    public System.Windows.Input.ICommand? BusyCancelCommand => CanCancelBusy() ? CancelBusyCommand : null;

    [RelayCommand(CanExecute = nameof(CanCancelBusy))]
    private void CancelBusy()
    {
        if (_busyOperationCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        BusyStatus = "Останавливаем операцию…";
        BusySubText = "Текущий файл будет безопасно завершён";
        CancelBusyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BusyCancelCommand));
    }

    private CancellationTokenSource BeginCancellableBusy(string status)
    {
        _busyOperationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _busyOperationCts = cts;
        _busyOperationStopwatch = Stopwatch.StartNew();
        BusyStatus = status;
        BusySubText = "Подготавливаем…";
        IsBusy = true;
        CancelBusyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BusyCancelCommand));
        return cts;
    }

    private IProgress<TransferProgress> BusyProgress(CancellationTokenSource owner) =>
        new Progress<TransferProgress>(p =>
        {
            if (!ReferenceEquals(_busyOperationCts, owner) || p.Total <= 0) return;
            int done = Math.Clamp(p.Done, 0, p.Total);
            double percent = done * 100.0 / p.Total;
            var elapsed = _busyOperationStopwatch?.Elapsed ?? TimeSpan.Zero;
            string eta = done > 0 && done < p.Total
                ? " · осталось ~" + FormatShortDuration(TimeSpan.FromSeconds(
                    elapsed.TotalSeconds / done * (p.Total - done)))
                : "";
            BusySubText = $"{done:N0} из {p.Total:N0} · {percent:0}%{eta}" +
                          (string.IsNullOrWhiteSpace(p.CurrentName) ? "" : $"\n{p.CurrentName}");
        });

    private void EndCancellableBusy(CancellationTokenSource owner)
    {
        if (!ReferenceEquals(_busyOperationCts, owner)) return;
        _busyOperationStopwatch?.Stop();
        _busyOperationStopwatch = null;
        _busyOperationCts = null;
        owner.Dispose();
        BusySubText = null;
        IsBusy = false;
        CancelBusyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BusyCancelCommand));
    }

    private static string FormatShortDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} ч {value.Minutes:00} мин";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes} мин {value.Seconds:00} с";
        return $"{Math.Max(0, value.Seconds)} с";
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isLoading || _busyOperationCts is not null) return;
        IsBusy = true;
        BusyStatus = Strings.Loading;
        BusySubText = null;
        // Стираем только ошибку ПРЕДЫДУЩЕЙ загрузки. Баннеры файловых операций должны пережить
        // последующее обновление коллекций (это их подтверждение/квитанция для пользователя).
        if (_loadErrorVisible)
        {
            OperationErrorText = null;
            _loadErrorVisible = false;
        }
        _isLoading = true;
        // Просмотрщик ссылается на VM файлов, которые ниже будут выброшены (Dispose миниатюр,
        // очистка коллекций) — закрываем и освобождаем его Bitmap-ы ДО перезагрузки.
        CloseViewer();
        try
        {
            _referenceFolders = _settings.Current.ReferenceFolders
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; } })
                .ToArray();

            var (exactTotal, imageTotal, audioTotal, mount, label, sid, preview) = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog();
                var mountById = catalog.Where(v => v.IsConnected && v.MountPath is not null)
                    .ToDictionary(v => v.Id, v => v.MountPath!);
                var labelById = catalog.ToDictionary(v => v.Id, v => v.DisplayName);

                // 2.8: предпросмотр — скан ещё идёт, но точные группы АКТИВНОЙ сессии уже целиком
                // в БД (ActiveExactReady). Показываем их, не дожидаясь похожих; все действия над
                // файлами отключены гейтами IsPreview (правило: удалять можно только из завершённой сессии).
                long? active = _scan.ActiveSessionId;
                bool isPreview = _scan.IsRunning && _scan.ActiveExactReady && active is not null;
                // P0.2: иначе показываем только результаты ПОСЛЕДНЕГО успешно завершённого скана. Если
                // завершённой сессии ещё нет (первый в жизни скан, ещё идёт) — session=null. ВАЖНО:
                // при null НЕ читаем группы «по всем сессиям», иначе частичные группы идущего скана показались
                // бы как готовый результат, из которого можно удалять (счёт = 0, страницы не грузим — guard в Append*).
                long? session = isPreview ? active : _sessions.GetLatestCompletedSessionId();
                // P1.1/P1: ВСЕ вкладки грузим ПОСТРАНИЧНО из БД (счёт групп + первая страница по запросу),
                // а не материализуем весь результат в память.
                int exTotal = session is null ? 0 : _groups.GetGroupCount(ExactTypes, session);
                // Похожие в предпросмотре не читаем вовсе: их группы строятся ПОЗЖЕ точных и были бы частичными.
                int imTotal = session is null || isPreview ? 0 : _groups.GetGroupCount(MatchType.SimilarImage, session);
                int auTotal = session is null || isPreview ? 0 : _groups.GetGroupCount(MatchType.SimilarAudio, session);
                return (exTotal, imTotal, auTotal, mountById, labelById, session, isPreview);
            });

            IsPreview = preview;
            PreviewBannerText = preview ? PreviewBannerMessage : null;
            if (preview) _previewLoaded = true; // предпросмотр уже на экране — не перезагружаться по событию
            _sessionFilter = sid;
            _exactTotal = exactTotal; _exactOffset = 0;
            _imageTotal = imageTotal; _imageOffset = 0;
            _audioTotal = audioTotal; _audioOffset = 0;
            _mountById = mount; _labelById = label;
            _imageBuilt = _audioBuilt = false;
            // Освобождаем нативную память миниатюр СТАРЫХ результатов (B5): Bitmap-ы без Dispose
            // копились между перезагрузками, GC их почти не собирал (малое managed-давление).
            DisposeGroupThumbnails(_allImage);
            DisposeGroupThumbnails(_allAudio);
            DisposeGroupThumbnails(ExactGroups);
            _allImage.Clear(); _allAudio.Clear();
            ExactGroups.Clear(); SimilarImageGroups.Clear(); SimilarAudioGroups.Clear();
            CanShowMoreSimilarImages = CanShowMoreSimilarAudio = false;

            // Точная вкладка открыта первой → показываем ПЕРВУЮ страницу из БД.
            AppendExactPage();
            ExactCountText = $"{_exactTotal}";
            ExactEmpty = _exactTotal == 0 ? Strings.NoGroups : null;
            // Счётчики похожих — всего найдено сканом (без декодирования миниатюр); слайдер порога сузит показ.
            ImageCountText = $"{_imageTotal}";
            AudioCountText = $"{_audioTotal}";
            // 2.8: в предпросмотре похожие вкладки честно объясняют, почему они пустые.
            ImageEmpty = preview ? PreviewSimilarPendingMessage : _imageTotal == 0 ? Strings.NoGroups : null;
            AudioEmpty = preview ? PreviewSimilarPendingMessage : _audioTotal == 0 ? Strings.NoGroups : null;

            // Если уже открыта вкладка похожих — построить её первую страницу сразу.
            if (SelectedTab == 1) EnsureImageBuilt();
            else if (SelectedTab == 2) EnsureAudioBuilt();

            DeleteSelectedCommand.NotifyCanExecuteChanged();
            UpdateMarkedSummary();
        }
        catch (Exception ex)
        {
            // LoadAsync вызывается из навигации и событий скана fire-and-forget. Поглощаем сбой
            // здесь, чтобы Task не остался ненаблюдаемым, и убираем потенциально устаревшие
            // карточки: после ошибки сопоставления томов действия над ними небезопасны.
            AppLog.Error("Results.Load", ex);
            OperationErrorText = UserFacingError.Describe(ex,
                "Не удалось загрузить результаты. Проверьте подключение дисков и повторите.");
            _loadErrorVisible = true;
            try { ClearLoadedResultsAfterFailure(); }
            catch (Exception cleanupEx) { AppLog.Error("Results.Load.Cleanup", cleanupEx); }
        }
        finally
        {
            // Без этого блок «Загрузка…» оставался бы навсегда при сбое чтения БД/тома.
            _isLoading = false;
            IsBusy = false;
        }
    }

    private void ClearLoadedResultsAfterFailure()
    {
        DisposeGroupThumbnails(_allImage);
        DisposeGroupThumbnails(_allAudio);
        DisposeGroupThumbnails(ExactGroups);
        _allImage.Clear();
        _allAudio.Clear();
        ExactGroups.Clear();
        SimilarImageGroups.Clear();
        SimilarAudioGroups.Clear();

        _sessionFilter = null;
        _exactTotal = _exactOffset = 0;
        _imageTotal = _imageOffset = 0;
        _audioTotal = _audioOffset = 0;
        _imageBuilt = _audioBuilt = false;
        CanShowMoreExact = CanShowMoreSimilarImages = CanShowMoreSimilarAudio = false;
        ExactCountText = ImageCountText = AudioCountText = "0";
        ExactEmpty = ImageEmpty = AudioEmpty = "Результаты временно недоступны.";
        IsPreview = false;
        PreviewBannerText = null;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UpdateMarkedSummary();
    }

    private static void DisposeGroupThumbnails(IEnumerable<GroupItemViewModel> groups)
    {
        foreach (var g in groups)
            foreach (var f in g.Files)
                f.DisposeThumbnail();
    }

    private GroupItemViewModel Build(DuplicateGroup g,
        Dictionary<long, string> mountById, Dictionary<long, string> labelById, bool withThumbnails = true)
    {
        // Точная группа разрешает удаление ТОЛЬКО если сверена байт-в-байт (Confidence>=1.0):
        // hash-only/офлайн exact (Confidence<1.0) — лишь информ., удалять нельзя, пока не перепроверено
        // (P0.2). Похожие — вручную (авто-отметку уже не даём). Видео — как точные.
        bool groupAllowsDeletion = g.MatchType switch
        {
            MatchType.Exact or MatchType.ExactVideo => g.Confidence >= 1.0,
            MatchType.SimilarImage or MatchType.SimilarAudio => true,
            _ => false
        };

        var rows = g.Members.Select(m =>
        {
            var f = m.File!;
            string? mount = mountById.GetValueOrDefault(f.VolumeId);
            string? full = mount is null ? null : Path.Combine(mount, f.RelPath);
            string? label = labelById.GetValueOrDefault(f.VolumeId);
            bool isRep = m.IsRepresentative || f.Id == g.RepresentativeId;
            bool prot = full is not null && IsUnderReference(full);
            return (Member: m, File: f, Full: full, Label: label, IsRep: isRep, Prot: prot);
        }).ToList();

        // 2.10: причина «почему эталон» — вычисляется заново при построении карточки
        // (в БД не хранится, схему не трогаем) и показывается тултипом бейджа «Эталон».
        string? repReason = ExplainRepresentative(rows);

        // 3.7: «Лучший кадр» — только для похожих ФОТО (серийная съёмка), см. FindSharpestId.
        long? sharpestId = g.MatchType == MatchType.SimilarImage
            ? FindSharpestId(rows.Select(r => r.File))
            : null;

        var files = rows.Select(r => new FileItemViewModel(r.File, r.Member.Similarity, r.IsRep,
            r.Full, r.Label, _thumbs, r.Prot, groupAllowsDeletion, loadThumbnail: withThumbnails,
            representativeReason: r.IsRep ? repReason : null,
            isSharpest: sharpestId is not null && r.File.Id == sharpestId));
        var gvm = new GroupItemViewModel(g, files);
        gvm.PropertyChanged += OnGroupPropertyChanged; // отметки → пересчёт доступности «Удалить»
        return gvm;
    }

    /// <summary>
    /// 3.7: порог заметности разницы резкости. Меньшая разница дисперсии Лапласиана на глаз
    /// неотличима (шум декода/пережатия) — бейдж «Лучший кадр» не показываем, а в просмотрщике
    /// пишем «примерно одинаковая».
    /// </summary>
    private const double SharpnessNoticeableRatio = 1.15;

    /// <summary>
    /// 3.7: id самого резкого кадра группы похожих фото — или null, если подсказка не убедительна:
    /// резкость посчитана меньше чем у двух членов (старые строки до пересчёта имеют NULL) или
    /// лидер не отрывается от второго места хотя бы на порог заметности.
    /// </summary>
    private static long? FindSharpestId(IEnumerable<FileRecord> files)
    {
        var ranked = files.Where(f => f.Sharpness is not null)
            .OrderByDescending(f => f.Sharpness!.Value)
            .Take(2)
            .ToList();
        return ranked.Count >= 2 && ranked[0].Sharpness!.Value > ranked[1].Sharpness!.Value * SharpnessNoticeableRatio
            ? ranked[0].Id
            : null;
    }

    /// <summary>
    /// 2.10: короткая причина выбора эталона для тултипа бейджа. Для правила Smart — объяснение
    /// KeepScorer по тем же кандидатам (включая признак «в папке-эталоне»); для простых правил —
    /// текст самого правила. Причина честна относительно ТЕКУЩЕЙ настройки: если правило сменили
    /// после скана, группы в БД могли строиться по-старому — но и тогда текст описывает логику,
    /// по которой эталон будет выбран при следующем скане.
    /// </summary>
    private string? ExplainRepresentative(
        List<(DuplicateMember Member, FileRecord File, string? Full, string? Label, bool IsRep, bool Prot)> rows)
    {
        int repIdx = rows.FindIndex(r => r.IsRep);
        if (repIdx < 0) return null;

        var rule = _settings.Current.DefaultRepresentativeRule;
        string reason;
        if (rule == RepresentativeRule.Smart)
        {
            var candidates = rows
                .Select(r => new KeepCandidate(r.File.RelPath, r.File.Name, r.File.Created,
                    r.File.Mtime, r.File.Size, r.Prot))
                .ToList();
            reason = KeepScorer.Explain(candidates, repIdx);
        }
        else
        {
            reason = rule switch
            {
                RepresentativeRule.Oldest => Strings.RuleOldest,
                RepresentativeRule.Newest => Strings.RuleNewest,
                RepresentativeRule.ShortestPath => Strings.RuleShortestPath,
                RepresentativeRule.LongestName => Strings.RuleLongestName,
                _ => Strings.RuleOldest
            };
        }
        return string.Format(Strings.RepresentativeReasonFmt, reason);
    }

    private bool IsUnderReference(string fullPath)
    {
        if (_referenceFolders.Length == 0) return false;
        string p;
        // Безопасный отказ: путь не разобрался — считаем защищённым (лучше не удалить, чем удалить лишнее).
        try { p = Path.GetFullPath(fullPath); } catch { return true; }
        foreach (var rf in _referenceFolders)
        {
            if (p.Equals(rf, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(rf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Достроить следующую страницу точных групп из БД (первая страница + «Показать ещё»).</summary>
    private void AppendExactPage(bool withThumbnails = true)
    {
        // Нет завершённой сессии → ничего не показываем (не читаем чужие/частичные группы). Ревью P7.
        if (_sessionFilter is null) { CanShowMoreExact = false; return; }
        var page = _groups.GetGroupsPage(ExactTypes, _sessionFilter, _exactOffset, PageSize);
        _exactOffset += page.Count;
        foreach (var g in page) ExactGroups.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreExact = ExactGroups.Count < _exactTotal;
    }

    [RelayCommand]
    private void ShowMoreExact() => AppendExactPage();

    /// <summary>
    /// Достроить ВСЕ точные группы — для действий «над всем»: авто-выбор и экспорт.
    /// БЕЗ миниатюр: эти VM строятся ради галочек/CSV, а жадное декодирование превью
    /// для десятков тысяч фото давало гигабайты Bitmap-ов и OOM (B5).
    /// </summary>
    private void EnsureAllExactBuilt()
    {
        while (ExactGroups.Count < _exactTotal)
        {
            int before = ExactGroups.Count;
            AppendExactPage(withThumbnails: false);
            if (ExactGroups.Count == before) break; // защита от зацикливания, если БД отдала меньше
        }
    }

    /// <summary>Построить ПЕРВУЮ страницу похожих фото при первом открытии вкладки (миниатюры — только для неё).</summary>
    private void EnsureImageBuilt()
    {
        if (_imageBuilt) return;
        _imageBuilt = true;
        _imageOffset = 0;
        _allImage.Clear();
        AppendSimilarImagePage();
    }

    private void EnsureAudioBuilt()
    {
        if (_audioBuilt) return;
        _audioBuilt = true;
        _audioOffset = 0;
        _allAudio.Clear();
        AppendSimilarAudioPage();
    }

    /// <summary>Достроить следующую страницу похожих фото из БД (миниатюры декодируются только для неё, P1).</summary>
    private void AppendSimilarImagePage(bool withThumbnails = true)
    {
        // 2.8: в предпросмотре похожие не читаем — их группы ещё строятся идущим сканом (частичны).
        if (_sessionFilter is null || IsPreview) { CanShowMoreSimilarImages = false; return; }
        var page = _groups.GetGroupsPage(MatchType.SimilarImage, _sessionFilter, _imageOffset, PageSize);
        _imageOffset += page.Count;
        foreach (var g in page) _allImage.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreSimilarImages = _imageOffset < _imageTotal;
        RefilterImages();
    }

    private void AppendSimilarAudioPage(bool withThumbnails = true)
    {
        // 2.8: см. AppendSimilarImagePage — в предпросмотре похожие вкладки пусты.
        if (_sessionFilter is null || IsPreview) { CanShowMoreSimilarAudio = false; return; }
        var page = _groups.GetGroupsPage(MatchType.SimilarAudio, _sessionFilter, _audioOffset, PageSize);
        _audioOffset += page.Count;
        foreach (var g in page) _allAudio.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreSimilarAudio = _audioOffset < _audioTotal;
        RefilterAudio();
    }

    [RelayCommand]
    private void ShowMoreSimilarImages() => AppendSimilarImagePage();

    [RelayCommand]
    private void ShowMoreSimilarAudio() => AppendSimilarAudioPage();

    /// <summary>Достроить ВСЕ похожие фото — для экспорта. Без миниатюр: они нужны только на экране (B5).</summary>
    private void EnsureAllSimilarImagesBuilt()
    {
        EnsureImageBuilt();
        while (_imageOffset < _imageTotal)
        {
            int before = _imageOffset;
            AppendSimilarImagePage(withThumbnails: false);
            if (_imageOffset == before) break; // защита от зацикливания, если БД отдала меньше
        }
    }

    private void EnsureAllSimilarAudioBuilt()
    {
        EnsureAudioBuilt();
        while (_audioOffset < _audioTotal)
        {
            int before = _audioOffset;
            AppendSimilarAudioPage(withThumbnails: false);
            if (_audioOffset == before) break;
        }
    }

    partial void OnImageThresholdChanged(double value)
    {
        if (_isLoading) return;
        if (_imageBuilt) RefilterImages();   // вкладка построена — перефильтровать загруженные страницы
        // иначе вкладка ещё не открыта: счётчик показывает «всего найдено», порог применится при открытии.
    }

    partial void OnAudioThresholdChanged(double value)
    {
        if (_isLoading) return;
        if (_audioBuilt) RefilterAudio();
    }

    private void RefilterImages()
    {
        var visible = _allImage.Where(g => g.TopSimilarity >= ImageThreshold).ToList();
        // Скрытая фильтром отметка опасна: пользователь больше не видит файл, но после обратного
        // сдвига порога она внезапно возвращалась. При скрытии группы безопасно снимаем её выбор.
        foreach (var g in _allImage.Except(visible))
            foreach (var f in g.Files)
                f.IsMarked = false;
        SimilarImageGroups.Clear();
        foreach (var g in visible)
            SimilarImageGroups.Add(g);
        // Счётчик в заголовке — ВСЕГО найдено сканом (стабилен, как у точной вкладки), а не отфильтрованная
        // страница (иначе число «сколько найдено» молча падало бы при пейджинге/слайдере).
        // Пустое состояние различаем: «ничего не найдено» vs «всё скрыто текущим порогом».
        // 2.8: в предпросмотре текст свой — похожие ещё не построены (Refilter мог быть вызван слайдером).
        ImageEmpty = IsPreview ? PreviewSimilarPendingMessage
            : _imageTotal == 0 ? Strings.NoGroups
            : SimilarImageGroups.Count == 0 ? "Похожие есть, но скрыты текущим порогом — сдвиньте слайдер влево."
            : null;
        // Порог мог скрыть группы с отметками — кнопки действий обязаны пересчитать доступность,
        // иначе остаются визуально активными и «не работают».
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
        UpdateMarkedSummary();
    }

    private void RefilterAudio()
    {
        var visible = _allAudio.Where(g => g.TopSimilarity >= AudioThreshold).ToList();
        foreach (var g in _allAudio.Except(visible))
            foreach (var f in g.Files)
                f.IsMarked = false;
        SimilarAudioGroups.Clear();
        foreach (var g in visible)
            SimilarAudioGroups.Add(g);
        AudioEmpty = IsPreview ? PreviewSimilarPendingMessage
            : _audioTotal == 0 ? Strings.NoGroups
            : SimilarAudioGroups.Count == 0 ? "Похожие есть, но скрыты текущим порогом — сдвиньте слайдер влево."
            : null;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
        UpdateMarkedSummary();
    }

    private IReadOnlyList<GroupItemViewModel> CurrentGroups() => SelectedTab switch
    {
        1 => SimilarImageGroups,
        2 => SimilarAudioGroups,
        _ => ExactGroups
    };

    /// <summary>Удалять можно, только если в текущей вкладке есть отмеченные файлы (и это не предпросмотр, 2.8).</summary>
    private bool CanDeleteSelected() => !IsPreview && CurrentGroups().Any(g => g.MarkedCount > 0);

    /// <summary>2.8: смена режима предпросмотра пересчитывает доступность всех действий над файлами.</summary>
    partial void OnIsPreviewChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
        AutoSelectAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (IsPreview) return; // 2.8: защита на случай вызова в обход CanExecute
        int count = CurrentGroups().Sum(g => g.MarkedCount);
        if (count == 0) return;
        // P2.3: явно перечисляем, что будет перемещено и что НЕ тронем (недоступные/непроверенные
        // файлы вообще нельзя отметить — но честно сообщаем их число в текущей вкладке).
        int blocked = CurrentGroups().SelectMany(g => g.Files).Count(f => !f.IsRepresentative && !f.CanMark);
        var extra = blocked > 0
            ? $"\nНедоступно сейчас или проверено не полностью — НЕ будет тронуто: {blocked}."
            : "";
        // P0-G: для вкладок «похожие» — отдельное предупреждение (это не идентичные файлы).
        var similarWarn = SelectedTab != 0
            ? "\n\n⚠ Это ПОХОЖИЕ, а не одинаковые файлы. Удаляйте только после ручной проверки."
            : "";
        // Честные тексты: пользователь карантина не должен читать «в Корзину».
        string dest = _settings.Current.UseRecycleBin
            ? "в Корзину Windows"
            : "в карантин программы (вернуть можно через раздел «История»)";
        UpdateMarkedSummary();
        var volume = MarkedSummaryText is null ? "" : $"\n{MarkedSummaryText}.";
        DeleteConfirmText = $"{Strings.ConfirmDeleteBody}\n\nБудет перемещено {dest}: {count}.{volume}{extra}{similarWarn}";
        ShowDeleteConfirm = true;
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsExactTab));
        OnPropertyChanged(nameof(IsSimilarTab));
        OnPropertyChanged(nameof(SelectionSafetyText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
        AutoSelectAllCommand.NotifyCanExecuteChanged(); // авто-выбор включён только на вкладке «точные»
        // Похожие вкладки строим ЛЕНИВО — миниатюры декодируются только при открытии вкладки.
        if (value == 1) EnsureImageBuilt();
        else if (value == 2) EnsureAudioBuilt();
        UpdateMarkedSummary();
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupItemViewModel.MarkedCount))
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            RenameSelectedCommand.NotifyCanExecuteChanged();
            DistributeSelectedCommand.NotifyCanExecuteChanged();
            UpdateMarkedSummary();
        }
    }

    /// <summary>«Отмечено: N файлов · X ГБ» — объём выбранного всегда на виду (и в диалоге).</summary>
    private void UpdateMarkedSummary()
    {
        int n = 0;
        long bytes = 0;
        foreach (var g in CurrentGroups())
            foreach (var f in g.MarkedFiles) { n++; bytes += f.Model.Size; }
        MarkedSummaryText = n == 0
            ? null
            : $"Отмечено: {Format.Count(n, "файл", "файла", "файлов")} · {Format.Bytes(bytes)}";
    }

    [RelayCommand]
    private void ClearAllMarks()
    {
        foreach (var g in CurrentGroups())
            foreach (var f in g.Files)
                f.IsMarked = false;
        UpdateMarkedSummary();
    }

    [RelayCommand]
    private void OpenJournal() => _nav.To<JournalViewModel>();

    /// <summary>
    /// Жадный подбор под цель «освободить N ГБ»: только сверенные байт-в-байт группы (Confidence 1.0),
    /// самые ёмкие первыми; эталон в каждой группе остаётся. Это лишь ПРЕДВЫБОР — удаление идёт через
    /// обычный диалог подтверждения и RevalidateBeforeDelete.
    /// </summary>
    [RelayCommand]
    private void FreeUpSelect()
    {
        // 2.8: подбор «под удаление» в предпросмотре не имеет смысла — удаление отключено.
        if (IsPreview) { OperationErrorText = PreviewActionBlockedMessage; return; }
        if (!double.TryParse(FreeUpGbText.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var gb) || gb <= 0)
        {
            OperationErrorText = "Укажите, сколько гигабайт нужно освободить (например 5).";
            return;
        }
        long target = (long)(gb * 1024 * 1024 * 1024);

        SelectedTab = 0;
        EnsureAllExactBuilt();
        foreach (var g in ExactGroups)
            foreach (var f in g.Files) f.IsMarked = false;

        long acc = 0;
        foreach (var g in ExactGroups
                     .Where(g => g.Model.Confidence >= 1.0)
                     .OrderByDescending(g => g.Model.ReclaimableBytes))
        {
            if (acc >= target) break;
            foreach (var f in g.Files)
            {
                if (f.IsRepresentative || f.IsProtected || !f.CanMark) continue;
                f.IsMarked = true;
                acc += f.Model.Size;
            }
        }

        UpdateMarkedSummary();
        OperationErrorText = null;
        OperationSuccessText = acc > 0
            ? $"Подобрано дубликатов на {Format.Bytes(acc)}. Проверьте список и нажмите кнопку удаления."
            : "Проверенных дубликатов для освобождения не нашлось — запустите поиск.";
    }

    [RelayCommand]
    private void CancelDelete() => ShowDeleteConfirm = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        // 2.8: в предпросмотре вся цепочка удаления недостижима ДАЖЕ программно — частичные
        // результаты идущего скана удалять нельзя (правило безопасности продукта). Guard здесь,
        // а не только в CanExecute: диалог мог быть «вооружён» до включения предпросмотра.
        if (IsPreview)
        {
            ShowDeleteConfirm = false;
            OperationErrorText = PreviewActionBlockedMessage;
            return;
        }
        ShowDeleteConfirm = false;
        bool useRecycle = _settings.Current.UseRecycleBin;

        OperationErrorText = null; OperationSuccessText = null;
        // P0-G: метод удаления ЗАЩИЩАЕТ СЕБЯ САМ, не полагаясь только на UI-гейтинг галочек.
        // CanMark = группа разрешает удаление И файл не защищён И путь доступен — повторно проверяем здесь.
        var toDelete = CurrentGroups()
            .SelectMany(g => g.MarkedFiles.Select(f => (group: g, file: f)))
            .Where(x => x.file.FullPath is not null)
            .Where(x => x.file.CanMark)
            .Where(x => !x.file.IsRepresentative) // эталон не удаляем никогда
            .ToList();
        if (toDelete.Count == 0) return;

        int moved = 0, failed = 0, blocked = 0, missing = 0;
        bool cancelled = false;
        var busy = BeginCancellableBusy("Проверяем и перемещаем выбранные файлы…");
        var progress = BusyProgress(busy);
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < toDelete.Count; i++)
                {
                    busy.Token.ThrowIfCancellationRequested();
                    var (group, file) = toDelete[i];
                    try
                    {
                        // P0.1: ПОЛНАЯ ПРОВЕРКА НА МОМЕНТ ДЕЙСТВИЯ — между сканом и удалением файл могли заменить,
                        // переместить или изменить (в т.ч. на содержимое ТОГО ЖЕ размера). Сверяем размер + время,
                        // а для точных/видео-групп — пересчитанный полный хэш. Не прошёл проверку — НЕ трогаем.
                        switch (RevalidateBeforeDelete(group, file))
                        {
                            case RevalidateResult.Missing: missing++; continue;
                            case RevalidateResult.Changed: blocked++; continue;
                        }

                        if (_fileOps.Delete(file.Model, file.FullPath!, useRecycle, group.Model.Id)) moved++;
                        else failed++;
                    }
                    finally
                    {
                        progress.Report(new TransferProgress(i + 1, toDelete.Count, file.Model.Name));
                    }
                }
            });
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally { EndCancellableBusy(busy); }

        string destDone = useRecycle ? "в Корзину" : "в карантин";
        if (cancelled || blocked > 0 || failed > 0 || missing > 0)
        {
            var msg = $"Перемещено {destDone}: {moved}.";
            if (cancelled) msg += " Операция остановлена по вашему запросу; остальные файлы не тронуты.";
            // Выдернутый диск — не «сбой»: файлы недоступны, но ничего не потеряно.
            if (missing > 0)
                msg += $" Недоступно — возможно, диск отключён (эти файлы не тронуты): {missing}.";
            if (blocked > 0)
                msg += $" Файлы изменились после проверки и не были тронуты — запустите проверку этой папки заново: {blocked}.";
            if (failed > 0) msg += $" Не удалось переместить: {failed}.";
            OperationErrorText = msg;
        }
        else if (moved > 0)
            OperationSuccessText = useRecycle
                ? $"Перемещено в Корзину: {moved}. Восстановить можно из Корзины Windows."
                : $"Перемещено в карантин: {moved}. Отменить можно в разделе «История».";

        if (_isActive) await LoadAsync();
    }

    /// <summary>Итог ревалидации перед удалением: Ok — можно удалять; Missing — файла уже нет
    /// (удалили или диск отключён) — не трогаем и сообщаем «недоступен», а не «изменился»;
    /// Changed — файл изменился/не сверился — не трогаем.</summary>
    private enum RevalidateResult { Ok, Missing, Changed }

    /// <summary>
    /// Ревалидация файла НА МОМЕНТ УДАЛЕНИЯ (P0.1). Удаляем только если файл не изменился с момента скана:
    /// существует, тот же размер, то же время изменения и создания, а для точных/видео-групп — совпадает
    /// ПЕРЕСЧИТАННЫЙ полный хэш с проиндексированным. Для похожих фото/музыки полный хэш по ТЗ не считаем,
    /// но размер и время всё равно сверяем (выбранный файл мог измениться после просмотра). Любой сбой
    /// проверки трактуем как «файл изменился» — и не трогаем его (лучше не удалить, чем удалить лишнее).
    /// </summary>
    private RevalidateResult RevalidateBeforeDelete(GroupItemViewModel group, FileItemViewModel file)
    {
        try
        {
            var m = file.Model;
            // Файл «ушёл в облако» (OneDrive-плейсхолдер): содержимое сверить нельзя, не скачав его,
            // а удаление плейсхолдера удаляет и облачную копию. Не трогаем (B4).
            if (CloudFiles.IsPlaceholderPath(file.FullPath!)) return RevalidateResult.Changed;
            var fi = new FileInfo(LongPath.Fix(file.FullPath!));
            if (!fi.Exists) return RevalidateResult.Missing;
            if (fi.Length != m.Size) return RevalidateResult.Changed;
            if (!TimesMatch(fi.LastWriteTimeUtc, m.Mtime)) return RevalidateResult.Changed;
            // Дату создания сверяем только если она была проиндексирована (старые строки могут быть пустыми).
            if (m.Created != default && !TimesMatch(fi.CreationTimeUtc, m.Created)) return RevalidateResult.Changed;

            bool exact = group.Model.MatchType is MatchType.Exact or MatchType.ExactVideo;
            if (exact && m.FullHash is not null)
            {
                var current = _hash.ComputeFullHash(file.FullPath!, default);
                if (!current.AsSpan().SequenceEqual(m.FullHash)) return RevalidateResult.Changed;
            }
            return RevalidateResult.Ok;
        }
        catch
        {
            // Сбой чтения посреди сверки: диск могли выдернуть прямо сейчас — если файла уже
            // не видно, честно говорим «недоступен» вместо «изменился».
            try { if (!File.Exists(LongPath.Fix(file.FullPath!))) return RevalidateResult.Missing; } catch { }
            return RevalidateResult.Changed;
        }
    }

    /// <summary>Сравнение времён с небольшим допуском: exFAT/FAT32 и сетевые ФС огрубляют mtime (до 1–2 c).</summary>
    private static bool TimesMatch(DateTime actualUtc, DateTimeOffset indexed) =>
        Math.Abs((actualUtc - indexed.UtcDateTime).TotalSeconds) <= 2.0;

    // --- P0-B: групповое переименование выбранных файлов ---
    private bool CanRenameSelected() => !IsPreview && CurrentGroups().Any(g => g.MarkedCount > 0);

    [RelayCommand(CanExecute = nameof(CanRenameSelected))]
    private void RenameSelected()
    {
        if (IsPreview) return; // 2.8: в предпросмотре действия над файлами отключены
        // Берём отмеченные файлы текущей вкладки как выбранный набор (отмечать можно только доступные).
        var marked = CurrentGroups().SelectMany(g => g.MarkedFiles)
            .Where(f => f.FullPath is not null).ToList();
        _renameSources = marked
            .Select(f => new RenameSourceFile(f.Model.Id, f.FullPath!, f.Model.Created, f.Model.Mtime,
                f.Model.Size, f.CanMark))
            .ToList();
        // Запоминаем том + точку монтирования каждого файла — нужно для пересчёта rel_path после rename.
        _renameMeta = new Dictionary<long, (long, string)>();
        foreach (var f in marked)
            _renameMeta[f.Model.Id] = (f.Model.VolumeId, _mountById.GetValueOrDefault(f.Model.VolumeId, ""));
        if (_renameSources.Count == 0) return;
        OperationErrorText = null; OperationSuccessText = null;

        RenameBaseName = "Файл";
        RenameStartNumber = 1;
        RefreshRenamePlan();
        ShowRenameDialog = true;
    }

    partial void OnRenameBaseNameChanged(string value) { if (ShowRenameDialog) RefreshRenamePlan(); }
    partial void OnRenameStartNumberChanged(int value) { if (ShowRenameDialog) RefreshRenamePlan(); }

    private void RefreshRenamePlan()
    {
        var req = new BatchRenameRequest(_renameSources, RenameBaseName,
            System.Math.Max(0, RenameStartNumber), 3, RenameSortOrder.Name);
        var plan = _rename.BuildPlan(req);
        _renamePlanField = plan;

        // Предпросмотр: первые строки «старое → новое».
        var lines = plan.Items.Where(i => !i.Skipped).Take(12)
            .Select(i => $"{i.OldName}  →  {i.NewName}");
        var preview = string.Join("\n", lines);
        int more = plan.Items.Count(i => !i.Skipped) - 12;
        if (more > 0) preview += $"\n… и ещё {more}";
        int skipped = plan.Items.Count(i => i.Skipped);
        if (skipped > 0) preview += $"\n(пропущено недоступных/защищённых: {skipped})";
        RenamePreviewText = preview;

        RenameConflictText = plan.Conflicts.Count > 0
            ? $"Конфликты имён: {plan.Conflicts.Count}. Измените имя или номер."
            : null;
        RenameCanApply = plan.CanExecute;
    }

    private BatchRenamePlan? _renamePlanField;

    [RelayCommand]
    private void DismissOperationBanner() { OperationErrorText = null; OperationSuccessText = null; }

    // --- P1.6: распределение результатов по папкам ---
    [ObservableProperty] private bool _showDistributeDialog;
    [ObservableProperty] private bool _distributeCopyMode; // false = только отчёт CSV (по умолчанию — безопасно)
    [ObservableProperty] private string? _distributeDestination;
    [ObservableProperty] private string _distributePreviewText = "";
    [ObservableProperty] private bool _distributeCanRun;
    private List<DistributionFile> _distributeSources = new();

    /// <summary>Устанавливается представлением — выбор папки назначения.</summary>
    public Func<Task<string?>>? PickDestinationFolder { get; set; }

    private bool CanDistribute() => !IsPreview && CurrentGroups().Any(g => g.MarkedCount > 0);

    [RelayCommand(CanExecute = nameof(CanDistribute))]
    private void DistributeSelected()
    {
        if (IsPreview) return; // 2.8: в предпросмотре действия над файлами отключены
        _distributeSources = CurrentGroups()
            .SelectMany(g => g.MarkedFiles)
            .Where(f => f.FullPath is not null)
            .Select(f => new DistributionFile(f.Model.Id, f.FullPath!, f.Model.Category.ToString(),
                f.Similarity, f.Model.Size))
            .ToList();
        if (_distributeSources.Count == 0) return;
        OperationErrorText = null; OperationSuccessText = null;
        DistributeCopyMode = false;
        RefreshDistributePreview();
        ShowDistributeDialog = true;
    }

    [RelayCommand]
    private async Task PickDistributeFolderAsync()
    {
        if (PickDestinationFolder is null) return;
        var folder = await PickDestinationFolder();
        if (!string.IsNullOrWhiteSpace(folder)) { DistributeDestination = folder; RefreshDistributePreview(); }
    }

    partial void OnDistributeCopyModeChanged(bool value) => RefreshDistributePreview();

    private void RefreshDistributePreview()
    {
        if (string.IsNullOrWhiteSpace(DistributeDestination))
        {
            DistributePreviewText = "Выберите папку назначения.";
            DistributeCanRun = false;
            return;
        }
        var req = new DistributionRequest(_distributeSources, DistributeDestination!,
            DistributeCopyMode ? DistributionMode.CopyFiles : DistributionMode.ReportOnly);
        var plan = _distribute.BuildPlan(req);
        var lines = plan.Items.Take(12).Select(i => $"{Path.GetFileName(i.SourcePath)}  →  {i.FolderKey}\\");
        DistributePreviewText = string.Join("\n", lines);
        if (plan.Items.Count > 12) DistributePreviewText += $"\n… и ещё {plan.Items.Count - 12}";
        DistributeCanRun = plan.CanExecute;
    }

    [RelayCommand]
    private void CancelDistribute() => ShowDistributeDialog = false;

    [RelayCommand]
    private async Task RunDistributeAsync()
    {
        // 2.8: guard как в ConfirmRenameAsync — диалог мог остаться от завершённой сессии.
        if (IsPreview) { ShowDistributeDialog = false; OperationErrorText = PreviewActionBlockedMessage; return; }
        if (string.IsNullOrWhiteSpace(DistributeDestination)) return;
        ShowDistributeDialog = false;
        bool copyMode = DistributeCopyMode;
        var req = new DistributionRequest(_distributeSources, DistributeDestination!,
            copyMode ? DistributionMode.CopyFiles : DistributionMode.ReportOnly);
        var busy = BeginCancellableBusy(copyMode ? "Копируем файлы…" : "Создаём отчёт…");
        DistributionResult? result = null;
        try
        {
            var progress = BusyProgress(busy);
            result = await Task.Run(() =>
            {
                var plan = _distribute.BuildPlan(req);
                return _distribute.Execute(plan, req, busy.Token, progress);
            });
        }
        catch (OperationCanceledException)
        {
            OperationErrorText = "Операция остановлена. Уже созданные копии сохранены, оригиналы не тронуты.";
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Results.Distribute", ex);
            OperationErrorText = UserFacingError.Describe(ex,
                "Распределение не удалось. Проверьте папку назначения и свободное место.");
            return;
        }
        finally { EndCancellableBusy(busy); }
        if (result is null) return;
        if (result.Error is not null)
            // Часть работы могла быть сделана (файлы скопированы, но отчёт не записался) — не теряем это.
            OperationErrorText = result.Copied > 0
                ? $"Скопировано: {result.Copied}, но {result.Error}"
                : $"Распределение не удалось: {result.Error}";
        else if (copyMode)
        {
            var msg = $"Скопировано: {result.Copied}";
            // Исчезнувший источник (диск отключён) отделяем от прочих сбоев копирования.
            if (result.Unavailable > 0) msg += $", недоступно (возможно, диск отключён): {result.Unavailable}";
            if (result.Failed > 0) msg += $", не удалось: {result.Failed}";
            msg += $". Оригиналы не тронуты. Отчёт: {result.ReportPath}";
            if (result.Unavailable > 0 || result.Failed > 0) OperationErrorText = msg;
            else OperationSuccessText = msg;
        }
        else
            OperationSuccessText = $"Отчёт сохранён: {result.ReportPath}. Файлы не двигались.";
    }

    [RelayCommand]
    private void CancelRename() => ShowRenameDialog = false;

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        // 2.8: план мог быть построен до включения предпросмотра — физическое переименование запрещено.
        if (IsPreview) { ShowRenameDialog = false; OperationErrorText = PreviewActionBlockedMessage; return; }
        if (_renamePlanField is null || !_renamePlanField.CanExecute) return;
        ShowRenameDialog = false;
        OperationErrorText = null; OperationSuccessText = null;
        var plan = _renamePlanField;
        var busy = BeginCancellableBusy("Переименовываем файлы…");
        BatchRenameResult result;
        try
        {
            var progress = BusyProgress(busy);
            result = await Task.Run(() => _rename.Execute(plan, busy.Token, progress));
        }
        catch (Exception ex)
        {
            AppLog.Error("Results.Rename", ex);
            OperationErrorText = UserFacingError.Describe(ex,
                "Переименование не удалось. Исходные имена сохранены или будут восстановлены при следующем запуске.");
            return;
        }
        finally { EndCancellableBusy(busy); }

        if (result.RolledBack)
        {
            // P0.4: ошибку показываем в видимом баннере страницы, а не в скрытом диалоге.
            var msg = $"Переименование отменено из-за ошибки: {result.Error}";
            if (result.Orphans.Count > 0)
                msg += "\nНе удалось вернуть имена автоматически (будет повторено при следующем запуске):\n" +
                       string.Join("\n", result.Orphans.Select(o =>
                           $"{o.CurrentPath}  →  исходное имя: {Path.GetFileName(o.OriginalPath)}"));
            OperationErrorText = msg;
            if (_isActive) await LoadAsync();
            return;
        }

        // P0.1: файлы переименованы физически → обновляем строки в БД, иначе UI ссылается на старые пути.
        int dbFailed = await Task.Run(() => UpdateIndexAfterRename(result.RenamedPairs));
        if (dbFailed > 0)
            OperationErrorText = $"Переименовано: {result.Renamed}, но сохранённый список файлов обновлён не полностью " +
                                 $"({dbFailed}). Запустите проверку этой папки заново. " +
                                 $"Детали сохранены в папке: {Path.Combine(_paths.RootDir, "recovery")}.";
        else
            OperationSuccessText = $"Переименовано: {result.Renamed}. Список файлов обновлён.";

        if (_isActive) await LoadAsync(); // перечитать (пути изменились)
    }

    /// <summary>Обновить строки БД по факту переименования. Возвращает число неудачных обновлений.</summary>
    private int UpdateIndexAfterRename(IReadOnlyList<RenamePair> pairs)
    {
        var failures = new List<(RenamePair pair, string reason)>();

        // Пакет собираем целиком и обновляем ОДНИМ вызовом (одна транзакция, два прохода):
        // по-файловое обновление при пересечении имён внутри пакета (сдвиг нумерации) удаляло
        // строку живого файла из индекса и маскировало это как успех.
        var updates = new List<RenameDbUpdate>(pairs.Count);
        foreach (var pair in pairs)
        {
            try
            {
                if (!_renameMeta.TryGetValue(pair.FileId, out var meta))
                { failures.Add((pair, "meta-missing")); continue; }
                if (string.IsNullOrEmpty(meta.mount))
                { failures.Add((pair, "empty-mount")); continue; }

                var fi = new FileInfo(pair.NewFullPath);
                var newRel = Path.GetRelativePath(meta.mount, pair.NewFullPath);
                var ext = Path.GetExtension(pair.NewFullPath);
                updates.Add(new RenameDbUpdate(pair.FileId, meta.volumeId, newRel,
                    Path.GetFileName(pair.NewFullPath), string.IsNullOrEmpty(ext) ? null : ext,
                    fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.Length));
            }
            catch (Exception ex) { failures.Add((pair, "meta-exception: " + ex.Message)); }
        }

        try
        {
            var failedIds = _files.UpdatePathsAfterRename(updates);
            foreach (var id in failedIds)
            {
                var pair = pairs.FirstOrDefault(p => p.FileId == id);
                if (pair is not null) failures.Add((pair, "db-row-not-updated"));
            }
        }
        catch (Exception ex)
        {
            foreach (var u in updates)
            {
                var pair = pairs.First(p => p.FileId == u.FileId);
                failures.Add((pair, "db-exception: " + ex.Message));
            }
        }

        // Safety (P0.1): физический rename прошёл, но БД не обновилась — пишем recovery-файл с путями + причиной.
        if (failures.Count > 0)
        {
            try
            {
                var dir = Path.Combine(_paths.RootDir, "recovery");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "rename_" + Guid.NewGuid().ToString("N") + ".json");
                var payload = failures.Select(f => new { f.pair.FileId, f.pair.OldFullPath, f.pair.NewFullPath, Reason = f.reason });
                SafeIo.WriteAllTextAtomic(file, System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* recovery — вспомогательное, не мешаем */ }
        }
        return failures.Count;
    }

    // --- Авто-выбор по правилу (оставить эталон, отметить остальные) ---
    // Доступно ТОЛЬКО на вкладке точных дубликатов. Похожие фото/музыка отмечаются исключительно
    // вручную: их «похожесть» не равна идентичности, автоудаление здесь опасно.
    // 2.8: в предпросмотре отключено — авто-отметка подталкивает к удалению, которое запрещено.
    private bool CanAutoSelect() => !IsPreview && SelectedTab == 0;

    [RelayCommand(CanExecute = nameof(CanAutoSelect))]
    private void AutoSelectAll()
    {
        if (IsPreview || SelectedTab != 0) return; // защита на случай вызова в обход CanExecute
        EnsureAllExactBuilt();        // «Выбрать все» действует на ВСЕ точные группы, а не только на стр. 1
        foreach (var g in ExactGroups)
        {
            // Hash-only/непроверенные группы (confidence < 1.0: есть офлайн/непрочитанные члены)
            // НЕ автоотмечаем — их можно удалять только вручную после перепроверки (раздел 2, P0).
            if (g.Model.Confidence < 1.0) continue;
            foreach (var f in g.Files)
                f.IsMarked = !f.IsRepresentative && !f.IsProtected;
        }
    }

    // --- Экспорт отчёта в CSV ---
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (PickSaveFile is null) return;
        var path = await PickSaveFile("deduplicator-report.csv");
        if (string.IsNullOrEmpty(path)) return;

        OperationErrorText = null;
        string tempPath = path + ".dedup-" + Guid.NewGuid().ToString("N") + ".tmp";
        var busy = BeginCancellableBusy("Сохраняем отчёт…");
        try
        {
            // Страницы читаются непосредственно из БД и сразу уходят в файл. Экспорт миллиона
            // строк больше не строит все UI-модели и один гигантский StringBuilder в памяти.
            int total = _sessionFilter is long sid
                ? await Task.Run(() => _groups.GetGroupCount(ExactTypes, sid) +
                                           _groups.GetGroupCount(MatchType.SimilarImage, sid) +
                                           _groups.GetGroupCount(MatchType.SimilarAudio, sid), busy.Token)
                : 0;
            var progress = BusyProgress(busy);
            int completed = 0;
            await using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(true), 64 * 1024))
            {
                await writer.WriteLineAsync("Тип;Группа;Эталон;Схожесть;Диск;Путь;Размер(байт)");
                completed = await AppendGroupsCsvAsync(writer, ExactTypes, Strings.TabExact,
                    completed, total, progress, busy.Token);
                completed = await AppendGroupsCsvAsync(writer, new[] { MatchType.SimilarImage }, Strings.TabSimilarImages,
                    completed, total, progress, busy.Token);
                await AppendGroupsCsvAsync(writer, new[] { MatchType.SimilarAudio }, Strings.TabSimilarAudio,
                    completed, total, progress, busy.Token);
            }
            File.Move(tempPath, path, overwrite: true);
            OperationSuccessText = $"Отчёт сохранён: {path}";
        }
        catch (OperationCanceledException)
        {
            OperationErrorText = "Сохранение отчёта остановлено. Незавершённый файл удалён.";
        }
        catch (Exception ex)
        {
            OperationErrorText = "Не удалось сохранить отчёт. Проверьте, не открыт ли файл в другой программе, и свободное место на диске.";
            AppLog.Error("Results.ExportCsv", ex);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            EndCancellableBusy(busy);
        }
    }

    private async Task<int> AppendGroupsCsvAsync(StreamWriter writer, IReadOnlyList<MatchType> types, string type,
        int completedBefore, int total, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        if (_sessionFilter is not long sessionId) return completedBefore;
        int offset = 0, groupNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await Task.Run(() => _groups.GetGroupsPage(types, sessionId, offset, PageSize), ct);
            if (page.Count == 0) return completedBefore + groupNumber;
            offset += page.Count;
            foreach (var group in page)
            {
                ct.ThrowIfCancellationRequested();
                groupNumber++;
                foreach (var member in group.Members)
                {
                    var file = member.File;
                    if (file is null) continue;
                    bool representative = member.IsRepresentative || member.FileId == group.RepresentativeId;
                    string volume = _labelById.TryGetValue(file.VolumeId, out var label)
                        ? label : $"Диск №{file.VolumeId}";
                    var line = string.Join(';',
                        Csv(type),
                        groupNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        representative ? "да" : "",
                        representative ? "" : Format.Percent(member.Similarity),
                        Csv(volume),
                        Csv(file.RelPath),
                        file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await writer.WriteLineAsync(line);
                }
                progress.Report(new TransferProgress(completedBefore + groupNumber, Math.Max(1, total), type));
            }
        }
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // --- Открыть папку с файлом ---
    [RelayCommand]
    private void OpenContaining(FileItemViewModel? file)
    {
        if (file?.FullPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
            { UseShellExecute = true });
        }
        catch { }
    }

    // --- Замена дублей жёсткими ссылками (NTFS) ---
    [ObservableProperty] private bool _showHardlinkConfirm;
    [ObservableProperty] private string _hardlinkConfirmText = "";
    private GroupItemViewModel? _hardlinkGroup;

    [RelayCommand]
    private void Hardlink(GroupItemViewModel? group)
    {
        if (group is null || IsPreview) return; // 2.8: в предпросмотре замена ссылками отключена
        _hardlinkGroup = group;
        HardlinkConfirmText = Strings.HardlinkWarning;
        ShowHardlinkConfirm = true;
    }

    [RelayCommand]
    private void CancelHardlink() { ShowHardlinkConfirm = false; _hardlinkGroup = null; }

    [RelayCommand]
    private async Task ConfirmHardlinkAsync()
    {
        ShowHardlinkConfirm = false;
        var group = _hardlinkGroup;
        _hardlinkGroup = null;
        // 2.8: диалог мог быть «вооружён» до включения предпросмотра — физическая замена файлов запрещена.
        if (group is null || IsPreview) return;

        var rep = group.Files.FirstOrDefault(f => f.IsRepresentative);
        if (rep?.FullPath is null) return;
        if (!File.Exists(rep.FullPath))
        {
            // Эталон мог исчезнуть, пока был открыт диалог (диск выдернули) — молчание выглядело
            // как «кнопка не работает». Ничего не изменено, файлы целы.
            OperationErrorText = "Эталон недоступен — возможно, диск отключён. Файлы не тронуты.";
            return;
        }

        var targets = group.Files
            .Where(f => !f.IsRepresentative && !f.IsProtected && f.FullPath is not null)
            .ToList();
        if (targets.Count == 0) return;

        int linked = 0, notLinked = 0;
        bool cancelled = false;
        var busy = BeginCancellableBusy("Заменяем копии жёсткими ссылками…");
        var progress = BusyProgress(busy);
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    busy.Token.ThrowIfCancellationRequested();
                    var f = targets[i];
                    // Сбой одного файла (в т.ч. исчезновение посреди пакета) не валит остальные:
                    // ReplaceWithHardLink сам проверяет пути/том/содержимое и без replaced-оригинала
                    // ничего не удаляет; страховочный catch — на случай сбоя БД при записи журнала.
                    bool ok;
                    try { ok = _fileOps.ReplaceWithHardLink(f.Model, f.FullPath!, rep.FullPath!, group.Model.Id); }
                    catch { ok = false; }
                    if (ok) linked++; else notLinked++;
                    progress.Report(new TransferProgress(i + 1, targets.Count, f.Model.Name));
                }
            });
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally { EndCancellableBusy(busy); }
        if (cancelled || notLinked > 0)
            OperationErrorText = $"Заменено ссылками: {linked}. Пропущено: {notLinked} " +
                                 (cancelled
                                     ? "Операция остановлена; остальные файлы не тронуты."
                                     : "(файл недоступен — возможно, диск отключён, содержимое изменилось или файл лежит на другом диске). Эти файлы не тронуты.");
        else if (linked > 0)
            OperationSuccessText = $"Заменено ссылками: {linked}. Место освобождено, содержимое файлов сохранено.";
        if (_isActive) await LoadAsync();
    }

    // --- Просмотрщик сравнения (текущий файл слева, эталон закреплён справа) ---
    // Работает для всех вкладок (точные/похожие) и в предпросмотре (просмотр безопасен; отметка,
    // как и везде, подчиняется CanMark). Не-изображения показываются иконкой категории + метаданными.
    [ObservableProperty] private bool _showViewer;
    [ObservableProperty] private FileItemViewModel? _viewerLeft;
    [ObservableProperty] private FileItemViewModel? _viewerRight;
    [ObservableProperty] private Bitmap? _viewerLeftImage;
    [ObservableProperty] private Bitmap? _viewerRightImage;
    [ObservableProperty] private string _viewerCounterText = "";
    [ObservableProperty] private string _viewerLeftResolution = "—";
    [ObservableProperty] private string _viewerRightResolution = "—";
    [ObservableProperty] private bool _viewerCanPrev;
    [ObservableProperty] private bool _viewerCanNext;
    // 3.7: сравнение резкости сторон («Резкость: выше/ниже/примерно одинаковая») — только для
    // групп похожих фото и только когда резкость посчитана у обеих сторон. null — строка скрыта.
    [ObservableProperty] private string? _viewerLeftSharpnessText;
    [ObservableProperty] private string? _viewerRightSharpnessText;
    private bool _viewerSimilarImageGroup;

    /// <summary>Слева листаются НЕ-эталонные файлы группы; эталон закреплён справа.</summary>
    private List<FileItemViewModel> _viewerFiles = new();
    private int _viewerIndex;
    // Поколения фоновых загрузок (свои на каждую сторону): при быстром листании/закрытии
    // «долетевший» Bitmap устаревшего запроса не публикуется, а сразу освобождается.
    private int _viewerLeftGen, _viewerRightGen;

    /// <summary>
    /// Предел декодирования полноразмерного показа по большей стороне: 24 МП целиком в память
    /// не тянем (паттерн B5), а больше ~1600 px на экране всё равно не видно.
    /// </summary>
    private const int ViewerDecodeSize = 1600;

    [RelayCommand]
    private void OpenViewer(FileItemViewModel? file)
    {
        if (file is null) return;
        // Группу ищем в ВИДИМОЙ коллекции текущей вкладки — плитка, по которой кликнули, там есть.
        var group = CurrentGroups().FirstOrDefault(g => g.Files.Contains(file));
        if (group is null) return;

        // Эталон всегда СПРАВА (стабильная точка сравнения); слева — остальные файлы группы.
        // Клик по самому эталону открывает сравнение с первым не-эталонным файлом.
        var right = group.Files.FirstOrDefault(f => f.IsRepresentative) ?? group.Files[0];
        var others = group.Files.Where(f => !ReferenceEquals(f, right)).ToList();
        if (others.Count == 0) others.Add(right); // защита: группа из одного файла (не должно быть)

        _viewerFiles = others;
        // 3.7: тексты резкости показываем только в группах похожих фото (в точных группах
        // содержимое байт-идентично, и «резкость одинаковая» была бы шумом).
        _viewerSimilarImageGroup = group.Model.MatchType == MatchType.SimilarImage;
        ViewerRight = right;
        _ = LoadViewerSideAsync(right, isLeft: false);
        SetViewerLeft(others.IndexOf(file)); // -1 (кликнули эталон) кламп даст 0
        ShowViewer = true;
    }

    /// <summary>Переключить левую сторону на файл с индексом <paramref name="index"/> (с клампом).</summary>
    private void SetViewerLeft(int index)
    {
        _viewerIndex = Math.Clamp(index, 0, _viewerFiles.Count - 1);
        var left = _viewerFiles[_viewerIndex];
        ViewerLeft = left;
        ViewerCounterText = $"{_viewerIndex + 1} {Strings.Of} {_viewerFiles.Count}";
        ViewerCanPrev = _viewerIndex > 0;
        ViewerCanNext = _viewerIndex < _viewerFiles.Count - 1;
        UpdateViewerSharpness();
        _ = LoadViewerSideAsync(left, isLeft: true);
    }

    /// <summary>
    /// 3.7: сравнение резкости текущей пары просмотрщика. Сырые числа пользователю не показываем
    /// (дисперсия Лапласиана ни о чём не говорит) — только понятный вывод «выше/ниже/примерно
    /// одинаковая» с тем же порогом заметности, что у бейджа «Лучший кадр».
    /// </summary>
    private void UpdateViewerSharpness()
    {
        string? leftText = null, rightText = null;
        if (_viewerSimilarImageGroup && ViewerLeft is { } l && ViewerRight is { } r &&
            !ReferenceEquals(l, r) &&
            l.Model.Sharpness is double ls && r.Model.Sharpness is double rs)
        {
            if (ls > rs * SharpnessNoticeableRatio) { leftText = Strings.SharpnessHigher; rightText = Strings.SharpnessLower; }
            else if (rs > ls * SharpnessNoticeableRatio) { leftText = Strings.SharpnessLower; rightText = Strings.SharpnessHigher; }
            else leftText = rightText = Strings.SharpnessSame;
        }
        ViewerLeftSharpnessText = leftText;
        ViewerRightSharpnessText = rightText;
    }

    [RelayCommand]
    private void ViewerPrev() { if (ShowViewer && ViewerCanPrev) SetViewerLeft(_viewerIndex - 1); }

    [RelayCommand]
    private void ViewerNext() { if (ShowViewer && ViewerCanNext) SetViewerLeft(_viewerIndex + 1); }

    [RelayCommand]
    private void CloseViewer()
    {
        ShowViewer = false;
        // Гонка с фоновым декодированием: поднимаем поколения, чтобы «в полёте» загрузки не публиковались.
        _viewerLeftGen++; _viewerRightGen++;
        _viewerFiles = new List<FileItemViewModel>();
        _viewerSimilarImageGroup = false;
        ViewerLeft = CreateViewerPlaceholder();
        ViewerRight = CreateViewerPlaceholder();
        var l = ViewerLeftImage; ViewerLeftImage = null; l?.Dispose();
        var r = ViewerRightImage; ViewerRightImage = null; r?.Dispose();
        ViewerLeftResolution = "—"; ViewerRightResolution = "—";
        ViewerLeftSharpnessText = null; ViewerRightSharpnessText = null;
    }

    private FileItemViewModel CreateViewerPlaceholder() => new(
        new FileRecord(), similarity: 1.0, isRepresentative: false,
        fullPath: null, volumeLabel: null, _thumbs,
        groupAllowsDeletion: false, loadThumbnail: false);

    /// <summary>
    /// Фоновая загрузка одной стороны: честное разрешение через Image.Identify (пиксели не
    /// декодируются) + ужатый Bitmap для показа. Кэшируем только текущую пару: старый Bitmap
    /// стороны освобождается сразу (паттерн DisposeThumbnail, B5). Отсутствующий/облачный/битый
    /// файл и не-изображения → null-Bitmap, view покажет иконку категории.
    /// </summary>
    private async Task LoadViewerSideAsync(FileItemViewModel file, bool isLeft)
    {
        int gen = isLeft ? ++_viewerLeftGen : ++_viewerRightGen;

        if (isLeft) { var old = ViewerLeftImage; ViewerLeftImage = null; old?.Dispose(); ViewerLeftResolution = "—"; }
        else { var old = ViewerRightImage; ViewerRightImage = null; old?.Dispose(); ViewerRightResolution = "—"; }

        string? path = file.FullPath;
        bool isImage = file.Model.Category == FileCategory.Image;

        var (bmp, resolution) = await Task.Run(() =>
        {
            Bitmap? b = null;
            string res = "—";
            try
            {
                // Облачный плейсхолдер не открываем: чтение заставило бы OneDrive скачивать файл (B4).
                if (path is null || CloudFiles.IsPlaceholderPath(path) || !File.Exists(LongPath.Fix(path)))
                    return (b, res);
                if (!isImage) return (b, res); // документы/музыка: иконка категории + метаданные

                var dims = _thumbs.GetDimensions(path);
                if (dims is { } d0) res = $"{d0.Width}×{d0.Height}";

                using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // Крупные фото декодируем сразу в ужатый размер по большей стороне; мелкие — как есть
                // (DecodeToWidth иначе РАСТЯНУЛ бы их, впустую раздув память).
                b = dims is { } d && Math.Max(d.Width, d.Height) > ViewerDecodeSize
                    ? (d.Width >= d.Height
                        ? Bitmap.DecodeToWidth(fs, ViewerDecodeSize)
                        : Bitmap.DecodeToHeight(fs, ViewerDecodeSize))
                    : new Bitmap(fs);
            }
            catch { b?.Dispose(); b = null; } // битый/недоступный файл → плейсхолдер-иконка
            return (b, res);
        });

        // Пока декодировали, пользователь мог перелистнуть или закрыть просмотрщик.
        bool stale = isLeft ? gen != _viewerLeftGen : gen != _viewerRightGen;
        if (stale || !ShowViewer) { bmp?.Dispose(); return; }
        if (isLeft) { ViewerLeftImage = bmp; ViewerLeftResolution = resolution; }
        else { ViewerRightImage = bmp; ViewerRightResolution = resolution; }
    }
}
