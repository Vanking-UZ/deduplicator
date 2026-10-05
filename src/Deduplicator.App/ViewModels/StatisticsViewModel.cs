using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Сводная статистика (экран 7).</summary>
public partial class StatisticsViewModel : PageViewModel
{
    private readonly IStatisticsService _stats;

    public override string Title => Strings.StatisticsTitle;

    public ObservableCollection<CategoryStat> Categories { get; } = new();

    [ObservableProperty] private string _exactGroupsText = "0";
    [ObservableProperty] private string _similarImagesText = "0";
    [ObservableProperty] private string _similarAudioText = "0";
    [ObservableProperty] private string _totalReclaimableText = "—";
    [ObservableProperty] private string _duplicateFilesText = "0";
    [ObservableProperty] private string _indexedText = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorText;

    public StatisticsViewModel(IStatisticsService stats) => _stats = stats;

    public override void OnNavigatedTo() => _ = LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = null;

        try
        {
            var s = await Task.Run(() => _stats.Compute());

            ExactGroupsText = $"{s.ExactGroups + s.ExactVideoGroups:N0}";
            SimilarImagesText = $"{s.SimilarImageGroups:N0}";
            SimilarAudioText = $"{s.SimilarAudioGroups:N0}";
            TotalReclaimableText = Format.Bytes(s.TotalReclaimableBytes);
            DuplicateFilesText = $"{s.TotalDuplicateFiles:N0}";
            IndexedText = $"{Format.Count(s.TotalIndexedFiles, "файл", "файла", "файлов")} · {Format.Bytes(s.TotalIndexedBytes)}";

            long maxBytes = s.BytesByCategory.Values.DefaultIfEmpty(0).Max();
            Categories.Clear();
            foreach (var kv in s.BytesByCategory.OrderByDescending(k => k.Value))
            {
                Categories.Add(new CategoryStat
                {
                    Category = kv.Key,
                    Bytes = kv.Value,
                    Count = s.FilesByCategory.GetValueOrDefault(kv.Key),
                    Fraction = maxBytes == 0 ? 0 : kv.Value / (double)maxBytes
                });
            }
        }
        catch (Exception ex)
        {
            ErrorText = UserFacingError.Describe(ex,
                "Не удалось обновить сводку. Повторите попытку; сохранённые результаты не изменены.");
            Deduplicator.Core.Util.AppLog.Error("Statistics.Load", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
