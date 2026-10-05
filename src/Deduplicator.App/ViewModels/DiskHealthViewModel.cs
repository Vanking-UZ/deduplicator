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

/// <summary>Здоровье дисков (SMART) — новый экран.</summary>
public partial class DiskHealthViewModel : PageViewModel
{
    private readonly IDiskHealthService _health;

    public override string Title => Strings.HealthTitle;

    public ObservableCollection<DiskHealthItemViewModel> Disks { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _anyPredictFailure;

    public DiskHealthViewModel(IDiskHealthService health) => _health = health;

    public override void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // #21: не запускаем второе обновление поверх идущего — иначе список мигает (Clear+заполнение дважды).
        if (IsBusy) return;
        IsBusy = true;
        Message = null;
        ErrorText = null;

        try
        {
            if (!_health.IsSupported)
            {
                Disks.Clear();
                AnyPredictFailure = false;
                Message = Strings.HealthUnsupported;
                return;
            }

            var disks = await Task.Run(() => _health.GetDisks());
            Disks.Clear();
            foreach (var d in disks) Disks.Add(new DiskHealthItemViewModel(d));

            AnyPredictFailure = disks.Any(d => d.PredictFailure);
            Message = disks.Count == 0 ? Strings.HealthNoData : null;
        }
        catch (Exception ex)
        {
            ErrorText = UserFacingError.Describe(ex,
                "Не удалось прочитать состояние дисков. Повторите попытку; сканирование файлов можно продолжать.");
            Deduplicator.Core.Util.AppLog.Error("DiskHealth.Refresh", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
