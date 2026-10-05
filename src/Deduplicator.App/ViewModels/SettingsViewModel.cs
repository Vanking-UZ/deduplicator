using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record ThemeOption(string Value, string Label);
public sealed record PerformanceModeOption(string Value, string Label, string Description);
public sealed record UiScaleOption(double Value, string Label);

/// <summary>Настройки приложения (экран 10).</summary>
public partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IAudioMatchService _audio;
    private readonly IUpdateService _update;
    private readonly ISentinelService _sentinel;
    private readonly IFileOpsService _fileOps;
    private readonly AppPaths _paths;
    private string? _releaseUrl;

    public override string Title => Strings.SettingsTitle;

    public ObservableCollection<RuleOption> Rules { get; } = new();
    public ObservableCollection<ThemeOption> Themes { get; } = new();
    public ObservableCollection<PerformanceModeOption> PerformanceModes { get; } = new();
    public ObservableCollection<UiScaleOption> UiScales { get; } = new();

    /// <summary>Устанавливается представлением — выбор папки карантина.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Устанавливается представлением — копирование текста в буфер обмена.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [ObservableProperty] private string _diagnosticsStatus = "";

    [ObservableProperty] private double _imageThreshold;
    [ObservableProperty] private double _audioThreshold;
    [ObservableProperty] private bool _useRecycleBin;
    [ObservableProperty] private bool _ecoMode; // 2.9: тихий фоновый режим для слабых машин
    [ObservableProperty] private bool _portableIndexEnabled; // 3.4: кэш проверки на съёмных носителях
    [ObservableProperty] private int _maxParallelism;
    [ObservableProperty] private PerformanceModeOption? _selectedPerformanceMode;
    [ObservableProperty] private bool _useExpertParallelism;
    [ObservableProperty] private bool _useElevatedMftHelper;
    [ObservableProperty] private bool _computePixelHash;
    [ObservableProperty] private RuleOption? _selectedRule;
    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private UiScaleOption? _selectedUiScale;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private string? _quarantinePath;
    [ObservableProperty] private string? _fpcalcPath;
    [ObservableProperty] private string _fpcalcStatus = "";
    [ObservableProperty] private bool _fpcalcAvailable;
    [ObservableProperty] private bool _saved;
    [ObservableProperty] private bool _rotationInvariant;
    [ObservableProperty] private int _minFileSizeMb;
    [ObservableProperty] private bool _skipHiddenSystem;
    [ObservableProperty] private string _excludedExtensions = "";
    [ObservableProperty] private bool _checkUpdatesEnabled;
    [ObservableProperty] private string _updateRepository = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _canOpenRelease;
    [ObservableProperty] private string _currentVersionText = "";

    public bool CanCheckUpdates
    {
        get
        {
            string repository = (UpdateRepository ?? string.Empty).Trim();
            int slash = repository.IndexOf('/');
            if (slash <= 0 || slash != repository.LastIndexOf('/') || slash == repository.Length - 1)
                return false;
            string[] parts = [repository[..slash], repository[(slash + 1)..]];
            return parts.All(static part =>
                part.Length > 0 && part.All(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
        }
    }

    public ObservableCollection<string> ReferenceFolders { get; } = new();

    // Сторожевой режим (Sentinel-lite): следить за новыми дубликатами, пока программа открыта.
    [ObservableProperty] private bool _sentinelEnabled;
    public ObservableCollection<string> SentinelFolders { get; } = new();

    public string ImagePercentText => Format.Percent(ImageThreshold);
    public string AudioPercentText => Format.Percent(AudioThreshold);
    public string PerformanceModeHint => UseExpertParallelism
        ? $"Ручная настройка: {Math.Max(1, MaxParallelism)} параллельных задач."
        : SelectedPerformanceMode?.Description ?? "Программа сама подберёт безопасную нагрузку.";

    /// <summary>Обратная сторона «Использовать Корзину»: удалять в выбранную папку. Двусторонне
    /// связана с UseRecycleBin, чтобы радиокнопки «Корзина» / «Выбранная папка» переключали флаг.</summary>
    public bool UseCustomFolder
    {
        get => !UseRecycleBin;
        set => UseRecycleBin = !value;
    }

    partial void OnUseRecycleBinChanged(bool value) => OnPropertyChanged(nameof(UseCustomFolder));
    partial void OnUpdateRepositoryChanged(string value) => OnPropertyChanged(nameof(CanCheckUpdates));

    public SettingsViewModel(ISettingsService settings, IAudioMatchService audio, IUpdateService update,
        ISentinelService sentinel, IFileOpsService fileOps, AppPaths paths)
    {
        _settings = settings;
        _audio = audio;
        _update = update;
        _sentinel = sentinel;
        _fileOps = fileOps;
        _paths = paths;
        CurrentVersionText = $"{Strings.CurrentVersionLabel}: {update.CurrentVersion}";

        // 2.10: «Умно» — первым в списке (рекомендация), но дефолт существующих пользователей
        // не меняем: AppSettings.DefaultRepresentativeRule остаётся Oldest.
        Rules.Add(new RuleOption(RepresentativeRule.Smart, Strings.RuleSmart));
        Rules.Add(new RuleOption(RepresentativeRule.Oldest, Strings.RuleOldest));
        Rules.Add(new RuleOption(RepresentativeRule.Newest, Strings.RuleNewest));
        Rules.Add(new RuleOption(RepresentativeRule.ShortestPath, Strings.RuleShortestPath));
        Rules.Add(new RuleOption(RepresentativeRule.LongestName, Strings.RuleLongestName));

        Themes.Add(new ThemeOption("Default", Strings.ThemeSystem));
        Themes.Add(new ThemeOption("Light", Strings.ThemeLight));
        Themes.Add(new ThemeOption("Dark", Strings.ThemeDark));

        PerformanceModes.Add(new PerformanceModeOption(
            PerformanceProfiles.Automatic,
            "Автоматически",
            "Рекомендуется: программа учитывает возможности компьютера, а обычный жёсткий диск всё равно читает последовательно."));
        PerformanceModes.Add(new PerformanceModeOption(
            PerformanceProfiles.Gentle,
            "Бережный",
            "Минимальная нагрузка — удобно, когда вы продолжаете работать за компьютером."));
        PerformanceModes.Add(new PerformanceModeOption(
            PerformanceProfiles.Maximum,
            "Максимальная скорость",
            "Использует доступные ядра активнее; компьютер может сильнее нагреваться и отвечать медленнее."));

        UiScales.Add(new UiScaleOption(1.0, "Обычный — 100%"));
        UiScales.Add(new UiScaleOption(1.125, "Крупный — 112%"));
        UiScales.Add(new UiScaleOption(1.25, "Очень крупный — 125%"));
        UiScales.Add(new UiScaleOption(1.5, "Максимальный — 150%"));

        LoadFromSettings();
    }

    public override void OnNavigatedTo()
    {
        LoadFromSettings();
        DiagnosticsText = BuildDiagnostics();
        Saved = false;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        ImageThreshold = s.DefaultImageThreshold;
        AudioThreshold = s.DefaultAudioThreshold;
        UseRecycleBin = s.UseRecycleBin;
        EcoMode = s.EcoMode;
        PortableIndexEnabled = s.PortableIndexEnabled;
        MaxParallelism = s.MaxParallelism;
        var mode = PerformanceProfiles.Normalize(s.PerformanceMode, s.EcoMode);
        SelectedPerformanceMode = PerformanceModes.FirstOrDefault(p => p.Value == mode) ?? PerformanceModes[0];
        UseExpertParallelism = string.IsNullOrWhiteSpace(s.PerformanceMode)
            ? !s.EcoMode // legacy: прежнее ручное MaxParallelism остаётся явно видимым как экспертное переопределение
            : s.UseExpertParallelism;
        UseElevatedMftHelper = s.UseElevatedMftHelper;
        ComputePixelHash = s.ComputePixelHash;
        SelectedRule = Rules.FirstOrDefault(r => r.Value == s.DefaultRepresentativeRule) ?? Rules[0];
        SelectedTheme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        SelectedUiScale = UiScales.OrderBy(x => Math.Abs(x.Value - s.UiScale)).First();
        ReduceMotion = s.ReduceMotion;
        QuarantinePath = s.QuarantinePath;
        FpcalcPath = s.FpcalcPath;
        FpcalcAvailable = _audio.IsAvailable;
        FpcalcStatus = _audio.IsAvailable ? Strings.FpcalcAvailable : Strings.FpcalcMissing;
        RotationInvariant = s.RotationInvariantImages;
        MinFileSizeMb = s.MinFileSizeMb;
        SkipHiddenSystem = s.SkipHiddenSystem;
        ExcludedExtensions = s.ExcludedExtensions;
        ReferenceFolders.Clear();
        foreach (var f in s.ReferenceFolders) ReferenceFolders.Add(f);
        SentinelEnabled = s.SentinelEnabled;
        SentinelFolders.Clear();
        foreach (var f in s.SentinelFolders) SentinelFolders.Add(f);
        CheckUpdatesEnabled = s.CheckUpdatesEnabled;
        UpdateRepository = s.UpdateRepository;
    }

    /// <summary>Текст диагностики, показываемый на экране настроек (P2.2).</summary>
    [ObservableProperty] private string _diagnosticsText = "";

    private string BuildDiagnostics() =>
        $"Deduplicator {_update.CurrentVersion}\n" +
        $"{Deduplicator.Core.Engine.EngineBuildInfo.Summary}\n" +
        $"IsAdmin={FastDedup.Engine.EngineEnvironment.IsElevated()} OS={Environment.OSVersion}\n" +
        $"RunId={Core.Util.AppLog.RunId}\n" +
        $"LogDir={RedactUserPath(_paths.LogDir)}\n" +
        $"DbPath={RedactUserPath(_paths.DbPath)}";

    private static string RedactUserPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        return profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    /// <summary>Собрать диагностику (версия/режим/права/пути) и скопировать в буфер — для поддержки (ТЗ MVP).</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        var text = BuildDiagnostics();
        DiagnosticsText = text;
        try { if (CopyToClipboard is not null) await CopyToClipboard(text); } catch { /* буфер недоступен */ }
        DiagnosticsStatus = Strings.DiagnosticsCopied;
    }

    /// <summary>Открыть папку логов в проводнике (P2.2) — ускоряет диагностику обращений в поддержку.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_paths.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = _paths.LogDir, UseShellExecute = true });
        }
        catch { /* не критично */ }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        CanOpenRelease = false;
        // Репозиторий скрыт из интерфейса — берём заданный пользователем или официальный по умолчанию.
        var repo = string.IsNullOrWhiteSpace(UpdateRepository)
            ? AppSettings.DefaultUpdateRepository
            : UpdateRepository.Trim();
        UpdateStatus = Strings.UpdateChecking;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _update.CheckAsync(repo, cts.Token);
            if (info is null) { UpdateStatus = Strings.UpdateError; return; }
            if (info.IsNewer)
            {
                UpdateStatus = $"{Strings.UpdateAvailable}: {info.LatestVersion}";
                _releaseUrl = info.Url;
                CanOpenRelease = !string.IsNullOrEmpty(info.Url);
            }
            else
            {
                UpdateStatus = Strings.UpdateUpToDate;
            }
        }
        catch
        {
            UpdateStatus = Strings.UpdateError;
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (string.IsNullOrEmpty(_releaseUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task AddReferenceFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (string.IsNullOrEmpty(folder)) return;
        // Нормализуем путь, как при проверке защиты, и сравниваем без учёта регистра.
        try { folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { }
        if (!ReferenceFolders.Contains(folder, System.StringComparer.OrdinalIgnoreCase))
        {
            ReferenceFolders.Add(folder);
            Saved = false; // изменение списка папок-эталонов — тоже несохранённая правка
        }
    }

    [RelayCommand]
    private void RemoveReferenceFolder(string? folder)
    {
        if (folder is not null && ReferenceFolders.Remove(folder)) Saved = false;
    }

    [RelayCommand]
    private async Task AddSentinelFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (string.IsNullOrEmpty(folder)) return;
        // Нормализация и сравнение — как у папок-эталонов (без учёта регистра).
        try { folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { }
        if (!SentinelFolders.Contains(folder, System.StringComparer.OrdinalIgnoreCase))
        {
            SentinelFolders.Add(folder);
            Saved = false;
        }
    }

    [RelayCommand]
    private void RemoveSentinelFolder(string? folder)
    {
        if (folder is not null && SentinelFolders.Remove(folder)) Saved = false;
    }

    partial void OnImageThresholdChanged(double value) => OnPropertyChanged(nameof(ImagePercentText));
    partial void OnAudioThresholdChanged(double value) => OnPropertyChanged(nameof(AudioPercentText));
    partial void OnSelectedPerformanceModeChanged(PerformanceModeOption? value) => OnPropertyChanged(nameof(PerformanceModeHint));
    partial void OnUseExpertParallelismChanged(bool value) => OnPropertyChanged(nameof(PerformanceModeHint));
    partial void OnMaxParallelismChanged(int value) => OnPropertyChanged(nameof(PerformanceModeHint));

    [RelayCommand]
    private async Task BrowseQuarantineAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (!string.IsNullOrEmpty(folder))
        {
            try { folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { }
            QuarantinePath = folder;
        }
    }

    /// <summary>Вернуть папку удалённых файлов к стандартной (в данных приложения).</summary>
    [RelayCommand]
    private void ResetQuarantineFolder() => QuarantinePath = _paths.QuarantineDir;

    /// <summary>Открыть папку удалённых файлов в Проводнике.</summary>
    [RelayCommand]
    private void OpenQuarantineFolder()
    {
        var dir = string.IsNullOrWhiteSpace(QuarantinePath) ? _paths.QuarantineDir : QuarantinePath!;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { Core.Util.AppLog.Error("OpenQuarantineFolder", ex); }
    }

    [RelayCommand]
    private void Save()
    {
        string performanceMode = SelectedPerformanceMode?.Value ?? PerformanceProfiles.Automatic;
        var s = new AppSettings
        {
            DefaultImageThreshold = Math.Round(ImageThreshold, 2),
            DefaultAudioThreshold = Math.Round(AudioThreshold, 2),
            UseRecycleBin = UseRecycleBin,
            MaxParallelism = Math.Max(1, MaxParallelism),
            PerformanceMode = performanceMode,
            UseExpertParallelism = UseExpertParallelism,
            UseElevatedMftHelper = UseElevatedMftHelper,
            ComputePixelHash = ComputePixelHash,
            DefaultRepresentativeRule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            Theme = SelectedTheme?.Value ?? "Default",
            UiScale = SelectedUiScale?.Value ?? 1.0,
            ReduceMotion = ReduceMotion,
            QuarantinePath = QuarantinePath,
            FpcalcPath = FpcalcPath,
            RotationInvariantImages = RotationInvariant,
            MinFileSizeMb = Math.Max(0, MinFileSizeMb),
            SkipHiddenSystem = SkipHiddenSystem,
            ExcludedExtensions = ExcludedExtensions ?? "",
            ReferenceFolders = ReferenceFolders.ToList(),
            SentinelEnabled = SentinelEnabled,
            SentinelFolders = SentinelFolders.ToList(),
            CheckUpdatesEnabled = CheckUpdatesEnabled,
            UpdateRepository = UpdateRepository ?? "",
            UpdatesConfigured = true, // пользователь настроил обновления вручную — миграцию больше не применяем
            // Поля, которых НЕТ на этом экране, обязаны переноситься из текущих настроек —
            // иначе «Сохранить» молча стирает выбор пользователя, сделанный в другом месте
            // (терялись категории с экрана сканирования).
            // Старое поле остаётся синхронизированным: существующие сервисы и старые версии приложения
            // продолжают понимать бережный режим без знания о новых профилях.
            EcoMode = !UseExpertParallelism && performanceMode == PerformanceProfiles.Gentle,
            PortableIndexEnabled = PortableIndexEnabled,
            OnboardingCompleted = _settings.Current.OnboardingCompleted,
            IncludedCategories = _settings.Current.IncludedCategories.ToList(),
            ExplorerViewMode = _settings.Current.ExplorerViewMode
        };
        _settings.Save(s);

        if (Application.Current is App app)
        {
            app.ApplyTheme(s.Theme);
            app.ApplyUiPreferences(s.UiScale, s.ReduceMotion);
        }

        // #25: путь к fpcalc применяется сразу, без перезапуска — и статус доступности обновляется.
        _audio.Reconfigure(s.FpcalcPath);
        FpcalcAvailable = _audio.IsAvailable;
        FpcalcStatus = _audio.IsAvailable ? Strings.FpcalcAvailable : Strings.FpcalcMissing;

        // Папка удалённых файлов применяется сразу (тот же паттерн): новые удаления пойдут в неё,
        // без перезапуска. Пустое значение = стандартная папка в данных приложения.
        var quarantine = string.IsNullOrWhiteSpace(s.QuarantinePath) ? _paths.QuarantineDir : s.QuarantinePath!;
        _fileOps.Reconfigure(quarantine);
        QuarantinePath = quarantine; // показать фактическую папку, если поле было пустым

        // Сторож применяется тоже сразу (тот же паттерн, что fpcalc/тема): включили — следим,
        // выключили или убрали все папки — останавливаемся. Ошибки не мешают сохранению настроек.
        try
        {
            if (s.SentinelEnabled && s.SentinelFolders.Count > 0) _sentinel.Start(s.SentinelFolders);
            else _sentinel.Stop();
        }
        catch (System.Exception ex) { Core.Util.AppLog.Error("ApplySentinel", ex); }

        Saved = true; // ставим последним, чтобы обновления статусов выше не сбросили бейдж
    }

    // #26: любое изменение настройки гасит бейдж «Сохранено», чтобы он не вводил в заблуждение.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Saved && e.PropertyName is not (
            nameof(Saved) or nameof(ImagePercentText) or nameof(AudioPercentText) or
            nameof(FpcalcStatus) or nameof(FpcalcAvailable) or nameof(UpdateStatus) or
            nameof(CanOpenRelease) or nameof(CurrentVersionText)))
            Saved = false;
    }
}
