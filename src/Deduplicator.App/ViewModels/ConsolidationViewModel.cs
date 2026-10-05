using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public sealed record RuleOption(RepresentativeRule Value, string Label);

/// <summary>Консолидация в чистый архив (экран 8).</summary>
public partial class ConsolidationViewModel : PageViewModel
{
    private readonly IConsolidationService _consolidation;
    private readonly IVolumeService _volumes;
    private CancellationTokenSource? _cts;

    public override string Title => Strings.ConsolidationTitle;

    public ObservableCollection<VolumeCheckItem> Sources { get; } = new();
    public ObservableCollection<RuleOption> Rules { get; } = new();
    public ObservableCollection<ConsolidationPreviewItem> Preview { get; } = new();

    /// <summary>Устанавливается представлением — выбор папки через системный диалог.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private RuleOption? _selectedRule;
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private bool _preserveStructure = true;
    // 3.5: объединять почти одинаковые фото (оставлять самый чёткий кадр). По умолчанию выключено.
    [ObservableProperty] private bool _collapseSimilarPhotos;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private string _uniqueText = "0";
    [ObservableProperty] private string _skippedText = "0";
    [ObservableProperty] private string _collapsedText = "0";
    [ObservableProperty] private string _bytesCopyText = "—";
    [ObservableProperty] private string _bytesSavedText = "—";
    [ObservableProperty] private double _execProgress;
    [ObservableProperty] private string? _execCurrent;
    [ObservableProperty] private string? _reportText;
    [ObservableProperty] private bool _isLoadingSources;
    [ObservableProperty] private string? _sourceLoadError;

    private ConsolidationPlan? _plan;

    public ConsolidationViewModel(IConsolidationService consolidation, IVolumeService volumes)
    {
        _consolidation = consolidation;
        _volumes = volumes;
        Rules.Add(new RuleOption(RepresentativeRule.Oldest, Strings.RuleOldest));
        Rules.Add(new RuleOption(RepresentativeRule.Newest, Strings.RuleNewest));
        Rules.Add(new RuleOption(RepresentativeRule.ShortestPath, Strings.RuleShortestPath));
        Rules.Add(new RuleOption(RepresentativeRule.LongestName, Strings.RuleLongestName));
        SelectedRule = Rules[0];
    }

    public override void OnNavigatedTo() => _ = LoadVolumesAsync();

    [RelayCommand]
    private async Task LoadVolumesAsync()
    {
        if (IsLoadingSources) return;
        IsLoadingSources = true;
        SourceLoadError = null;

        try
        {
            var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
            Sources.Clear();
            foreach (var v in catalog) Sources.Add(new VolumeCheckItem(v));
        }
        catch (Exception ex)
        {
            SourceLoadError = UserFacingError.Describe(ex,
                "Не удалось загрузить список дисков. Проверьте подключение и нажмите «Повторить».");
            Deduplicator.Core.Util.AppLog.Error("Consolidation.LoadVolumes", ex);
        }
        finally
        {
            IsLoadingSources = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (!string.IsNullOrEmpty(folder)) TargetPath = folder;
    }

    [RelayCommand]
    private async Task BuildPlanAsync()
    {
        var ids = Sources.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        // #23: молчаливое бездействие заменяем понятной подсказкой, что именно не заполнено.
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(TargetPath))
        {
            ReportText = Strings.ConsolidationNeedInput;
            return;
        }

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = ids,
            TargetPath = TargetPath!,
            Rule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            PreserveFolderStructure = PreserveStructure,
            CollapseSimilarPhotos = CollapseSimilarPhotos
        };

        ReportText = null;
        var plan = await Task.Run(() => _consolidation.BuildPlan(options));
        _plan = plan;

        UniqueText = $"{plan.UniqueCount:N0}";
        SkippedText = $"{plan.SkippedCount:N0}";
        CollapsedText = $"{plan.CollapsedCount:N0}";
        BytesCopyText = Format.Bytes(plan.BytesToCopy);
        BytesSavedText = Format.Bytes(plan.BytesSaved);

        Preview.Clear();
        foreach (var item in plan.ToCopy.Take(200))
            Preview.Add(new ConsolidationPreviewItem(item, true));
        // 3.5: схлопнутые похожие показываем явно — что не поедет и какой кадр оставлен.
        foreach (var item in plan.CollapsedSimilar.Take(100))
            Preview.Add(new ConsolidationPreviewItem(item, false));
        foreach (var item in plan.ToSkip.Take(100))
            Preview.Add(new ConsolidationPreviewItem(item, false));

        HasPlan = true;
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (_plan is null || string.IsNullOrWhiteSpace(TargetPath)) return;

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = Sources.Where(s => s.IsChecked).Select(s => s.Id).ToList(),
            TargetPath = TargetPath!,
            Rule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            PreserveFolderStructure = PreserveStructure,
            CollapseSimilarPhotos = CollapseSimilarPhotos
        };

        _cts = new CancellationTokenSource();
        IsExecuting = true;
        ReportText = null;

        try
        {
            var report = await Task.Run(() => _consolidation.Execute(_plan, options, (done, total, name) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ExecProgress = total == 0 ? 0 : done * 100.0 / total;
                    ExecCurrent = name;
                });
            }, _cts.Token));

            // 3.5: счётчик схлопнутых похожих — в итоге, как и в плане.
            var collapsedPart = report.CollapsedSimilar > 0
                ? $"{Strings.PlanCollapsed}: {report.CollapsedSimilar:N0}, "
                : string.Empty;
            ReportText = $"{Strings.ConsolidationDone}. {Strings.Copied}: {report.Copied:N0}, " +
                         $"{Strings.PlanSkipped}: {report.Skipped:N0}, {collapsedPart}" +
                         $"{Strings.FailedItems}: {report.Failed:N0}. " +
                         $"{Strings.PlanBytesCopy}: {Format.Bytes(report.BytesCopied)}";
            // Причины ошибок («Носитель отключён: …») были только в отчёте-объекте и до
            // пользователя не доходили — показываем первые несколько прямо под итогом.
            if (report.Errors.Count > 0)
            {
                var shown = report.Errors.Take(3);
                ReportText += "\n" + string.Join("\n", shown);
                if (report.Errors.Count > 3) ReportText += $"\n… и ещё {report.Errors.Count - 3}";
            }
        }
        catch (OperationCanceledException)
        {
            // #22: после отмены UI не должен зависнуть в состоянии «выполняется».
            ReportText = Strings.ConsolidationCancelled;
        }
        catch (Exception ex)
        {
            ReportText = UserFacingError.Describe(ex, "Не удалось выполнить план. Исходные файлы сохранены.");
            Deduplicator.Core.Util.AppLog.Error("Consolidation.Execute", ex);
        }
        finally
        {
            IsExecuting = false;
            ExecCurrent = null;
        }
    }

    [RelayCommand]
    private void CancelExecute() => _cts?.Cancel();
}

/// <summary>Строка предпросмотра плана консолидации.</summary>
public sealed class ConsolidationPreviewItem
{
    public ConsolidationPreviewItem(ConsolidationItem item, bool willCopy)
    {
        Name = item.File.Name;
        Source = item.File.VolumeLabel ?? $"Диск №{item.File.VolumeId}";
        // 3.5: у схлопнутого похожего кадра показываем, какой файл поехал вместо него.
        if (item.CollapsedInto is not null)
        {
            Target = $"{Strings.CollapsedKeptPrefix}: {item.CollapsedInto.Name}";
            ActionText = Strings.CollapsedBadge;
        }
        else
        {
            Target = willCopy ? item.TargetRelPath : "—";
            ActionText = willCopy ? Strings.PlanUnique : Strings.PlanSkipped;
        }
        SizeText = Format.Bytes(item.File.Size);
        WillCopy = willCopy;
    }

    public string Name { get; }
    public string Source { get; }
    public string Target { get; }
    public string SizeText { get; }
    public string ActionText { get; }
    public bool WillCopy { get; }
}
