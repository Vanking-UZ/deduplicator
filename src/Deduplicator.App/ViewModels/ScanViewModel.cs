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
using Deduplicator.Core.Audio;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.App.ViewModels;

public sealed record DepthOption(ScanDepth Value, string Label, bool Enabled);
public sealed record StrictnessOption(int Index, string Label);

/// <summary>Экран запуска сверки и отображения прогресса (экраны 3–4).</summary>
public partial class ScanViewModel : PageViewModel
{
    private readonly IScanService _scan;
    private readonly IVolumeService _volumes;
    private readonly ISettingsService _settings;
    private readonly IAudioMatchService _audioService;
    private readonly NavigationService _nav;

    private CancellationTokenSource? _cts;
    private long? _pendingPreselect;
    private bool _loadingVolumes;

    public override string Title => Strings.ScanTitle;
    public Func<Task<string?>>? PickFolder { get; set; }

    public ObservableCollection<VolumeCheckItem> Volumes { get; } = new();
    public ObservableCollection<DepthOption> DepthOptions { get; } = new();
    public ObservableCollection<StrictnessOption> StrictnessOptions { get; } = new();

    [ObservableProperty] private DepthOption? _selectedDepth;
    [ObservableProperty] private StrictnessOption? _selectedStrictness;

    // P0-A «Что искать»: какие категории файлов проверять. Всегда все — экран проверки без ручной
    // настройки (просьба пользователя «просто ищет все файлы»), поэтому эти флаги остаются true.
    [ObservableProperty] private bool _includeImages = true;
    [ObservableProperty] private bool _includeVideo = true;
    [ObservableProperty] private bool _includeAudio = true;
    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includeOther = true;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string? _failText;
    [ObservableProperty] private bool _isCancelled; // Отмена — явное состояние с карточкой

    /// <summary>Настройка и финальный экран — разные продуктовые состояния.
    /// Раньше после завершения весь мастер настройки снова появлялся над итогом.</summary>
    public bool IsSetupVisible => !IsRunning && !IsDone;

    /// <summary>Крупная сводка выбора рядом с главным действием (одиночный выбор диска).</summary>
    public string SelectedVolumesSummary
    {
        get
        {
            var selected = Volumes.FirstOrDefault(v => v.IsChecked);
            if (selected is null) return "Диск не выбран";
            return selected.HasScanFolder
                ? $"Диск: {selected.DisplayName} · выбранная папка"
                : $"Диск: {selected.DisplayName} · проверка целиком";
        }
    }

    /// <summary>Спокойное описание того, что произойдёт: ищем всё, настраивать ничего не нужно.</summary>
    public string ScanOptionsSummary =>
        "Ищем одинаковые файлы среди всех типов и похожие фотографии. Ничего настраивать не нужно — просто нажмите «Начать поиск».";

    [ObservableProperty] private string _stageTitle = "";
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _filesSeenText = "0";
    [ObservableProperty] private string _filesHashedText = "0";
    [ObservableProperty] private string _skippedText = "0";
    [ObservableProperty] private string _groupsText = "0";
    [ObservableProperty] private string? _emptyHint;
    [ObservableProperty] private string? _infoText;
    [ObservableProperty] private string _elapsedText = "0 с";
    [ObservableProperty] private string _estimatedRemainingText = "Считаем…";
    [ObservableProperty] private string _bytesPerSecondText = "—";
    [ObservableProperty] private string _filesPerSecondText = "—";

    public ScanViewModel(IScanService scan, IVolumeService volumes, ISettingsService settings,
        IAudioMatchService audioService, NavigationService nav)
    {
        _scan = scan;
        _volumes = volumes;
        _settings = settings;
        _audioService = audioService;
        _nav = nav;

        DepthOptions.Add(new DepthOption(ScanDepth.ExactOnly, Strings.DepthExact, true));
        DepthOptions.Add(new DepthOption(ScanDepth.WithSimilarImages, Strings.DepthImages, true));
        DepthOptions.Add(new DepthOption(ScanDepth.WithSimilarImagesAndAudio, Strings.DepthAudio, _audioService.IsAvailable));
        // «Найти всё» без ручной настройки: по умолчанию ищем одинаковые файлы среди ВСЕХ типов
        // + похожие фото (главная ценность продукта). Похожую музыку не включаем автоматически —
        // это медленный внешний fpcalc; точные аудио-дубликаты всё равно находит обычный проход.
        SelectedDepth = DepthOptions.FirstOrDefault(o => o.Value == ScanDepth.WithSimilarImages && o.Enabled)
                        ?? DepthOptions.First(o => o.Enabled);

        // P1.5: строгость поиска похожих (профиль).
        StrictnessOptions.Add(new StrictnessOption(0, Strings.StrictnessStrict));
        StrictnessOptions.Add(new StrictnessOption(1, Strings.StrictnessNormal));
        StrictnessOptions.Add(new StrictnessOption(2, Strings.StrictnessWide));
        SelectedStrictness = StrictnessOptions[1]; // Обычно
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsSetupVisible));
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(IsSetupVisible));

    public void Preselect(long volumeId) => _pendingPreselect = volumeId;

    /// <summary>Предвыбор глубины (подсказка «носитель с фотографиями»): применяется, только если
    /// режим доступен, и не сбрасывается при навигации — пользователь видит его сразу.</summary>
    public void PreselectDepth(ScanDepth depth)
    {
        var opt = DepthOptions.FirstOrDefault(o => o.Value == depth && o.Enabled);
        if (opt is not null) SelectedDepth = opt;
    }

    public override void OnNavigatedTo()
    {
        // Экран проверки без ручной настройки: всегда ищем во всех типах файлов.
        if (!IsRunning)
        {
            IncludeImages = IncludeVideo = IncludeAudio = IncludeDocuments = IncludeOther = true;
            _ = LoadVolumesAsync();
        }
    }

    /// <summary>Собрать выбранные категории (P0-A). Пустой набор означает ошибку валидации.</summary>
    private HashSet<FileCategory> BuildIncludedCategories()
    {
        var set = new HashSet<FileCategory>();
        if (IncludeImages) set.Add(FileCategory.Image);
        if (IncludeVideo) set.Add(FileCategory.Video);
        if (IncludeAudio) set.Add(FileCategory.Audio);
        if (IncludeDocuments) set.Add(FileCategory.Document);
        if (IncludeOther) set.Add(FileCategory.Other);
        return set;
    }

    /// <summary>Запомнить выбор категорий в настройках, чтобы он сохранялся между запусками.</summary>
    private void SaveCategoriesToSettings(HashSet<FileCategory> categories)
    {
        try
        {
            _settings.Current.IncludedCategories = categories.Select(c => c.ToString()).ToList();
            _settings.Save(_settings.Current);
        }
        catch { /* настройки — не критично для самого скана */ }
    }

    private async Task LoadVolumesAsync()
    {
        if (_loadingVolumes) return;
        _loadingVolumes = true;
        try
        {
            var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
            var connected = catalog.Where(v => v.IsConnected).ToList();

            foreach (var item in Volumes) item.PropertyChanged -= OnCheckChanged;
            Volumes.Clear();
            foreach (var v in connected)
            {
                var item = new VolumeCheckItem(v);
                item.PropertyChanged += OnCheckChanged;
                Volumes.Add(item); // без отметки — одиночный выбор задаём ровно одним элементом ниже
            }

            // Одиночный выбор: ровно ОДИН диск выбран по умолчанию. Приоритет — предвыбранный том
            // (подсказка «флешка с фото»), иначе первый несистемный диск, иначе первый доступный.
            long? pre = _pendingPreselect;
            _pendingPreselect = null;
            int initialIdx = -1;
            if (pre is long id) initialIdx = connected.FindIndex(v => v.Id == id);
            if (initialIdx < 0) initialIdx = connected.FindIndex(ShouldSelectByDefault);
            if (initialIdx < 0 && Volumes.Count > 0) initialIdx = 0;
            if (initialIdx >= 0) Volumes[initialIdx].IsChecked = true;

            EmptyHint = connected.Count == 0 ? Strings.ScanNoVolumes : null;
            OnPropertyChanged(nameof(SelectedVolumesSummary));
            StartCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            // Этот путь запускается из навигации fire-and-forget. Ошибка чтения БД/носителей
            // не должна становиться ненаблюдаемым Task и оставлять активным устаревший выбор.
            foreach (var item in Volumes) item.PropertyChanged -= OnCheckChanged;
            Volumes.Clear();
            EmptyHint = UserFacingError.Describe(ex,
                "Не удалось обновить список дисков. Проверьте подключение носителей и повторите.");
            AppLog.Error("Scan.LoadVolumes", ex);
            OnPropertyChanged(nameof(SelectedVolumesSummary));
        }
        finally
        {
            _loadingVolumes = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private bool _singleSelectGuard;

    private void OnCheckChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VolumeCheckItem.IsChecked)) return;

        // Одиночный выбор (радиокнопочная логика): выбрали диск — снимаем выбор с остальных.
        if (!_singleSelectGuard && sender is VolumeCheckItem it && it.IsChecked)
        {
            _singleSelectGuard = true;
            foreach (var v in Volumes)
                if (!ReferenceEquals(v, it) && v.IsChecked) v.IsChecked = false;
            _singleSelectGuard = false;
        }

        OnPropertyChanged(nameof(SelectedVolumesSummary));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ChooseFolderAsync(VolumeCheckItem? item)
    {
        if (item is null || PickFolder is null) return;

        var folder = await PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        folder = NormalizeDirectory(folder);
        if (!Directory.Exists(folder))
        {
            EmptyHint = "Выбранная папка недоступна. Подключите диск или выберите другую папку.";
            return;
        }

        if (!IsPathInsideRoot(folder, item.MountPath))
        {
            EmptyHint = "Выберите папку внутри выбранного диска.";
            return;
        }

        item.ScanFolder = folder;
        item.IsChecked = true;
        EmptyHint = null;
        OnPropertyChanged(nameof(SelectedVolumesSummary));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearFolder(VolumeCheckItem? item)
    {
        if (item is null) return;
        item.ScanFolder = null;
        OnPropertyChanged(nameof(SelectedVolumesSummary));
    }

    private bool CanStart() => !IsRunning && Volumes.Any(v => v.IsChecked);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var selected = Volumes.Where(v => v.IsChecked).ToList();
        if (selected.Count == 0) return;

        // #8: носитель мог быть извлечён после загрузки списка — не запускаем «пустую» сверку молча.
        var connectedById = (await Task.Run(() => _volumes.SyncAndGetCatalog()))
            .Where(v => v.IsConnected && !string.IsNullOrWhiteSpace(v.MountPath))
            .ToDictionary(v => v.Id);
        if (!selected.Any(v => connectedById.ContainsKey(v.Id)))
        {
            EmptyHint = Strings.ScanVolumeUnavailable;
            _ = LoadVolumesAsync();
            return;
        }

        // Раньше блокировался старт, только если недоступны ВСЕ выбранные тома,
        // а частично отвалившийся (выдернутая флешка) молча выпадал — скан завершался «успехом» и
        // затирал прежнюю полную сессию, включая группы по отсутствующему диску. Теперь честно
        // останавливаемся и называем недоступные тома; пользователь сам решит — переподключить
        // или снять галочку.
        var missing = selected.Where(v => !connectedById.ContainsKey(v.Id)).ToList();
        if (missing.Count > 0)
        {
            EmptyHint = "Эти диски сейчас недоступны: " +
                        string.Join(", ", missing.Select(m => m.DisplayName)) +
                        ". Подключите их или снимите отметку — иначе результаты будут неполными.";
            _ = LoadVolumesAsync();
            return;
        }

        var ids = new List<long>();
        var folders = new List<string>();
        foreach (var item in selected)
        {
            if (!connectedById.TryGetValue(item.Id, out var volume)) continue;

            if (string.IsNullOrWhiteSpace(item.ScanFolder))
            {
                ids.Add(item.Id);
                continue;
            }

            var folder = NormalizeDirectory(item.ScanFolder);
            if (!Directory.Exists(folder) || !IsPathInsideRoot(folder, volume.MountPath))
            {
                EmptyHint = $"Папка для диска {item.DisplayName} недоступна. Выберите папку заново или сбросьте выбор.";
                return;
            }

            folders.Add(folder);
        }

        if (ids.Count == 0 && folders.Count == 0)
        {
            EmptyHint = Strings.ScanVolumeUnavailable;
            _ = LoadVolumesAsync();
            return;
        }

        var categories = BuildIncludedCategories();
        if (categories.Count == 0)
        {
            EmptyHint = Strings.PickAtLeastOneCategory; // P0-A: нельзя сканировать «ничего»
            return;
        }
        SaveCategoriesToSettings(categories); // запомнить выбор между запусками

        IsFailed = false;
        FailText = null;
        IsCancelled = false;
        var s = _settings.Current;
        var options = new ScanOptions
        {
            VolumeIds = ids,
            Folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Depth = SelectedDepth?.Value ?? ScanDepth.ExactOnly,
            Parallelism = PerformanceProfiles.ResolveParallelism(s, Environment.ProcessorCount),
            Incremental = true,
            ImageThreshold = s.DefaultImageThreshold,
            AudioThreshold = s.DefaultAudioThreshold,
            MinFileSize = (long)System.Math.Max(0, s.MinFileSizeMb) * 1024 * 1024,
            SkipHiddenSystem = s.SkipHiddenSystem,
            ExcludedExtensions = ParseExtensions(s.ExcludedExtensions),
            IncludedCategories = categories
        };

        // P1.5: профиль строгости похожих (порог) — применяется только в режимах поиска похожих.
        if (options.Depth >= ScanDepth.WithSimilarImages)
        {
            var preset = (SelectedStrictness?.Index ?? 1) switch
            {
                0 => SimilarityProfileService.Strict(),
                2 => SimilarityProfileService.Wide(),
                _ => SimilarityProfileService.Normal()
            };
            options.ImageProfile = preset;
            options.AudioProfile = preset;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsDone = false;
        IsPaused = false;
        InfoText = null;
        ProgressIndeterminate = true;
        ElapsedText = "0 с";
        EstimatedRemainingText = "Считаем…";
        BytesPerSecondText = "—";
        FilesPerSecondText = "—";
        StartCommand.NotifyCanExecuteChanged();

        _scan.ProgressChanged += OnProgress;
        Exception? startFailure = null;
        bool cancelledBeforeTerminalState = false;
        try
        {
            await _scan.RunAsync(options, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена до фактического старта задачи (Task.Run с ct) — не сбой.
            cancelledBeforeTerminalState = true;
        }
        catch (Exception ex)
        {
            // RunCore переводит сбои в состояние Failed сам, но исключение может прилететь и вне
            // его try (например, БД недоступна при создании сессии). Без catch оно уходило из
            // async-команды и роняло приложение «в рабочий стол».
            Core.Util.AppLog.Error("ScanViewModel.StartAsync", ex);
            startFailure = ex;
            IsFailed = true;
            FailText = UserFacingError.Describe(ex, "Проверку не удалось завершить. Прежние результаты сохранены — попробуйте ещё раз.");
            Deduplicator.Core.Util.AppLog.Error("ScanViewModel.Start", ex);
        }
        finally
        {
            _scan.ProgressChanged -= OnProgress;
            IsRunning = false;
            IsPaused = false;
            ProgressIndeterminate = false;
            var terminalStage = _scan.Progress.Stage;
            IsDone = startFailure is null && !cancelledBeforeTerminalState &&
                     terminalStage == ScanStage.Completed;
            HasResults = IsDone;
            // #6: ошибка сверки больше не молчит — показываем понятное состояние.
            IsFailed = startFailure is not null || terminalStage == ScanStage.Failed;
            if (startFailure is not null)
                FailText = UserFacingError.Describe(startFailure,
                    "Проверку не удалось завершить. Прежние результаты сохранены — попробуйте ещё раз.");
            else if (terminalStage == ScanStage.Failed)
                FailText = "Проверка остановлена. Прежние результаты сохранены. Проверьте, что носитель подключён и доступен, затем повторите. Подробности есть в журнале диагностики.";
            // Отмена — не молчаливое исчезновение прогресса, а явное состояние.
            IsCancelled = startFailure is null &&
                          (cancelledBeforeTerminalState || terminalStage == ScanStage.Cancelled);
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnProgress(ScanProgress p) => Dispatcher.UIThread.Post(() => Apply(p));

    private void Apply(ScanProgress p)
    {
        StageTitle = p.StageTitle;
        CurrentFile = p.CurrentFile;
        FilesSeenText = p.EstimatedTotalFiles is > 0
            ? $"{p.FilesSeen:N0} из ~{p.EstimatedTotalFiles.Value:N0}"
            : $"{p.FilesSeen:N0}";
        FilesHashedText = $"{p.FilesHashed:N0}";
        SkippedText = $"{p.FilesSkipped:N0}";
        GroupsText = $"{p.GroupsFound:N0}";
        ProgressIndeterminate = p.OverallFraction is null &&
                                p.Stage is not (ScanStage.Completed or ScanStage.Cancelled or ScanStage.Failed);
        ProgressValue = (p.OverallFraction ?? p.Fraction ?? 0) * 100;
        ElapsedText = FormatDuration(p.Elapsed);
        EstimatedRemainingText = p.EstimatedRemaining is { } remaining
            ? "~" + FormatDuration(remaining)
            : p.Stage is ScanStage.Completed or ScanStage.Cancelled or ScanStage.Failed ? "—" : "Считаем…";
        BytesPerSecondText = p.BytesPerSecond > 0
            ? $"{Format.Bytes((long)p.BytesPerSecond)}/с"
            : "—";
        FilesPerSecondText = p.FilesPerSecond > 0
            ? $"{p.FilesPerSecond:N0} файл/с"
            : "—";
        // Информационное сообщение во время скана (напр. «быстрый режим MFT недоступен»). На ошибке
        // его место занимает FailText, поэтому здесь показываем только не-ошибочные сообщения.
        if (p.Stage != ScanStage.Failed) InfoText = p.Message;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours} ч {value.Minutes:00} мин";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes} мин {value.Seconds:00} с";
        return $"{Math.Max(0, value.Seconds)} с";
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning) return;
        if (IsPaused) { _scan.Resume(); IsPaused = false; }
        else { _scan.Pause(); IsPaused = true; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ViewResults() => _nav.To<ResultsViewModel>();

    [RelayCommand]
    private void NewScan()
    {
        IsDone = false;
        IsFailed = false;
        IsCancelled = false;
        FailText = null;
        InfoText = null;
        CurrentFile = null;
        _ = LoadVolumesAsync();
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

    private static System.Collections.Generic.HashSet<string> ParseExtensions(string? csv)
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var raw in csv.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var e = raw.Trim();
            if (!e.StartsWith('.')) e = "." + e;
            set.Add(e);
        }
        return set;
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathInsideRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var fullPath = NormalizeDirectory(path);
            var fullRoot = Path.GetFullPath(root);
            var rootWithoutSlash = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSlash = fullRoot.EndsWith(Path.DirectorySeparatorChar) ||
                                fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            return string.Equals(fullPath, rootWithoutSlash, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
