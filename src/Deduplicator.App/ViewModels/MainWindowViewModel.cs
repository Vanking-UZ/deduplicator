using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Пункт боковой навигации.</summary>
public sealed record NavItem(string Title, string IconKey, PageViewModel Page, string Tip);

public enum OnboardingStepKind
{
    Welcome,
    Spotlight,
    Final
}

/// <summary>Шаг обучения: заголовок, текст, на какой раздел переключиться,
/// имя подсвечиваемого контрола (x:Name) и короткая подсказка-указатель.</summary>
public sealed record OnboardingStep(
    string Title,
    string Body,
    int NavIndex,
    string TargetName,
    string Hint,
    OnboardingStepKind Kind = OnboardingStepKind.Spotlight,
    bool WaitForAction = false);

/// <summary>Главная вью-модель: боковая навигация и активная страница.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ScanViewModel _scan;
    private readonly IReadOnlyList<OnboardingStep> _onboardingSteps;
    private readonly Stack<PageViewModel> _backStack = new();

    public string AppName => Strings.AppName;
    public string AppTagline => Strings.AppTagline;
    public string HelpText => Strings.HelpButton;
    public FeedbackPickerViewModel Feedback { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private bool _isOnboardingVisible;
    [ObservableProperty] private int _onboardingIndex;

    // У активной страницы открыт диалог-оверлей → MainWindow затемняет и
    // блокирует сайдбар (раньше диалог накрывал только область страницы, сайдбар оставался
    // кликабельным), а Esc закрывает диалог через CloseCurrentDialog().
    [ObservableProperty] private bool _isDialogOpen;

    private bool _suppress;
    private bool _suppressHistory;

    public MainWindowViewModel(
        NavigationService nav,
        ISettingsService settingsService,
        FeedbackPickerViewModel feedback,
        HomeViewModel home,
        VolumesViewModel volumes,
        ExplorerViewModel explorer,
        ScanViewModel scan,
        ResultsViewModel results,
        StatisticsViewModel statistics,
        ConsolidationViewModel consolidation,
        JournalViewModel journal,
        SettingsViewModel settings,
        DiskHealthViewModel diskHealth,
        CleanupViewModel cleanup,
        CrossDiskViewModel crossDisk)
    {
        _settings = settingsService;
        _scan = scan;
        Feedback = feedback;
        NavItems = new ObservableCollection<NavItem>
        {
            new("Главная", "IconGrid", home, "Крупные действия и краткая сводка"),
            new("Найти копии", "IconSearch", scan, "Проверить выбранные диски на одинаковые и похожие файлы"),
            new("Результаты", "IconLayers", results, "Посмотреть найденные копии и убрать лишнее"),
            new("Мои диски", "IconHdd", volumes, "Подключённые и сохранённые диски"),
            // История остаётся на первом уровне: восстановление удалённого нельзя прятать.
            // Специализированные инструменты доступны крупными карточками на Главной — десять
            // равноправных пунктов перегружали первого пользователя.
            new("История", "IconHistory", journal, "Что было убрано и куда — отсюда можно вернуть файлы"),
            new(Strings.NavSettings, "IconSettings", settings, "Основные параметры безопасности и поиска"),
        };

        _onboardingSteps = new[]
        {
            new OnboardingStep(
                "Добро пожаловать!",
                "Deduplicator помогает спокойно находить лишние копии файлов и освобождать место. Рекомендуемые безопасные настройки уже включены — для первого поиска ничего настраивать не нужно.",
                0, "", "", OnboardingStepKind.Welcome),
            new OnboardingStep(
                "1. Выберите, где искать",
                "Теперь можно работать прямо под подсказкой. Проверьте автоматически выбранный диск, при необходимости выберите другой и нажмите «Далее».",
                1, "OnboardingScanDisks", "Проверьте выбранный диск и продолжайте"),
            new OnboardingStep(
                "2. Запустите безопасную проверку",
                "Нажмите «Начать поиск». На этом этапе программа только читает файлы и ищет совпадения — ничего не удаляется.",
                1, "OnboardingScanStart", "Нажмите кнопку — следующий шаг откроется сам",
                WaitForAction: true),
            new OnboardingStep(
                "Всё готово",
                "Всё готово к первой проверке. Если поиск уже запущен, следите за процентом и оставшимся временем — программу можно свернуть. Если вы пропустили шаг, начать можно большой кнопкой ниже. После завершения откройте «Результаты» и спокойно проверьте список перед удалением.",
                1, "", "", OnboardingStepKind.Final),
        };

        nav.Navigator = SetPage;
        SelectedNav = NavItems[0];
        IsOnboardingVisible = !_settings.Current.OnboardingCompleted;
        _scan.PropertyChanged += OnScanOnboardingChanged;
        RefreshOnboardingProperties();
    }

    public int OnboardingStepNumber => OnboardingIndex + 1;
    public int OnboardingStepCount => _onboardingSteps.Count;
    public string OnboardingProgressText => $"Шаг {OnboardingStepNumber} из {OnboardingStepCount}";
    public double OnboardingProgress => OnboardingStepCount == 0 ? 0 : OnboardingStepNumber * 100.0 / OnboardingStepCount;
    public string OnboardingTitle => _onboardingSteps[OnboardingIndex].Title;
    public string OnboardingBody => _onboardingSteps[OnboardingIndex].Body;
    public string OnboardingHint => _onboardingSteps[OnboardingIndex].Hint;
    public string OnboardingTargetName => _onboardingSteps[OnboardingIndex].TargetName;
    public bool OnboardingHasTarget => !string.IsNullOrEmpty(OnboardingTargetName);
    public bool IsOnboardingWelcome => _onboardingSteps[OnboardingIndex].Kind == OnboardingStepKind.Welcome;
    public bool IsOnboardingFinal => _onboardingSteps[OnboardingIndex].Kind == OnboardingStepKind.Final;
    public bool IsOnboardingActionStep => _onboardingSteps[OnboardingIndex].WaitForAction;
    public string OnboardingNextText => IsOnboardingFinal
        ? "Готово"
        : IsOnboardingActionStep
            ? "Сделаю позже"
            : IsOnboardingWelcome ? "Начать знакомство" : "Далее";
    public bool CanGoBack => OnboardingIndex > 0;
    public bool CanNavigateBack => _backStack.Count > 0;
    public string BackButtonText => _backStack.TryPeek(out var page) ? $"Назад: {page.Title}" : "Назад";
    public bool IsToolPage => CurrentPage is not null &&
                              NavItems.All(n => !ReferenceEquals(n.Page, CurrentPage));

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (_suppress || value is null) return;
        SetPage(value.Page);
    }

    private void SetPage(PageViewModel page)
    {
        var previous = CurrentPage;
        if (ReferenceEquals(previous, page)) return;
        if (previous is not null && !_suppressHistory)
            _backStack.Push(previous);
        if (!ReferenceEquals(previous, page)) previous?.OnNavigatedFrom();

        // Следим за HasOpenDialog ТЕКУЩЕЙ страницы (отписка от прежней обязательна —
        // иначе чужой диалог продолжал бы дёргать IsDialogOpen и копились бы подписки).
        if (previous is not null) previous.PropertyChanged -= OnPageDialogChanged;
        page.PropertyChanged += OnPageDialogChanged;

        _suppress = true;
        CurrentPage = page;
        SelectedNav = NavItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        _suppress = false;
        IsDialogOpen = page.HasOpenDialog;
        OnPropertyChanged(nameof(IsToolPage));
        RefreshNavigationProperties();
        page.OnNavigatedTo();
    }

    private void OnPageDialogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.HasOpenDialog))
            IsDialogOpen = CurrentPage?.HasOpenDialog ?? false;
    }

    partial void OnIsDialogOpenChanged(bool value)
    {
        if (value) Feedback.ClosePanelCommand.Execute(null);
    }

    partial void OnIsOnboardingVisibleChanged(bool value)
    {
        if (value) Feedback.ClosePanelCommand.Execute(null);
    }

    /// <summary>Esc из MainWindow: закрыть верхний диалог активной страницы.</summary>
    public void CloseCurrentDialog() => CurrentPage?.CloseDialog();

    [RelayCommand]
    private void NavigateBack()
    {
        if (!_backStack.TryPop(out var page)) return;
        _suppressHistory = true;
        try { SetPage(page); }
        finally { _suppressHistory = false; }
        RefreshNavigationProperties();
    }

    [RelayCommand]
    private void ShowOnboarding()
    {
        Feedback.ClosePanelCommand.Execute(null);
        OnboardingIndex = 0;
        IsOnboardingVisible = true;
        ApplyOnboardingStep();
        RefreshOnboardingProperties();
    }

    [RelayCommand]
    private void NextOnboarding()
    {
        if (OnboardingIndex >= _onboardingSteps.Count - 1)
        {
            CompleteOnboarding(openSearch: false);
            return;
        }

        OnboardingIndex++;
    }

    [RelayCommand]
    private void PreviousOnboarding()
    {
        if (OnboardingIndex <= 0) return;
        OnboardingIndex--;
    }

    [RelayCommand]
    private void SkipOnboarding() => CompleteOnboarding(openSearch: false);

    /// <summary>Закрытие обучения по Esc. Повтор остаётся доступен в боковом меню.</summary>
    public void CloseOnboarding() => CompleteOnboarding(openSearch: false);

    partial void OnOnboardingIndexChanged(int value)
    {
        ApplyOnboardingStep();
        RefreshOnboardingProperties();
    }

    private void ApplyOnboardingStep()
    {
        if (_onboardingSteps.Count == 0) return;
        // Переключаемся на соответствующий шагу раздел — он виден затемнённым позади карточки.
        var index = _onboardingSteps[OnboardingIndex].NavIndex;
        if (index >= 0 && index < NavItems.Count)
        {
            _suppressHistory = true;
            try { SelectedNav = NavItems[index]; }
            finally { _suppressHistory = false; }
        }
    }

    private void CompleteOnboarding(bool openSearch)
    {
        IsOnboardingVisible = false;
        var s = _settings.Current;
        s.OnboardingCompleted = true;
        _settings.Save(s);

        if (openSearch && NavItems.Count > 1)
        {
            _suppressHistory = true;
            try { SelectedNav = NavItems[1]; }
            finally { _suppressHistory = false; }
        }
    }

    private void RefreshOnboardingProperties()
    {
        OnPropertyChanged(nameof(OnboardingStepNumber));
        OnPropertyChanged(nameof(OnboardingStepCount));
        OnPropertyChanged(nameof(OnboardingProgressText));
        OnPropertyChanged(nameof(OnboardingProgress));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingBody));
        OnPropertyChanged(nameof(OnboardingHint));
        OnPropertyChanged(nameof(OnboardingTargetName));
        OnPropertyChanged(nameof(OnboardingHasTarget));
        OnPropertyChanged(nameof(IsOnboardingWelcome));
        OnPropertyChanged(nameof(IsOnboardingFinal));
        OnPropertyChanged(nameof(IsOnboardingActionStep));
        OnPropertyChanged(nameof(OnboardingNextText));
        OnPropertyChanged(nameof(CanGoBack));
    }

    private void OnScanOnboardingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsOnboardingVisible || !IsOnboardingActionStep) return;

        bool selectedDisk = OnboardingIndex == 1 &&
                            e.PropertyName == nameof(ScanViewModel.SelectedVolumesSummary) &&
                            _scan.Volumes.Any(v => v.IsChecked);
        bool startedScan = OnboardingIndex == 2 &&
                           e.PropertyName == nameof(ScanViewModel.IsRunning) &&
                           _scan.IsRunning;

        if (selectedDisk || startedScan)
            OnboardingIndex++;
    }

    private void RefreshNavigationProperties()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(BackButtonText));
    }
}
