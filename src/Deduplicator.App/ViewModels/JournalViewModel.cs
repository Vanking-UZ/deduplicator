using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Журнал операций и восстановление из карантина (экран 9).</summary>
public partial class JournalViewModel : PageViewModel
{
    private const int PageSize = 100;
    private readonly OperationLogRepository _log;
    private readonly IFileOpsService _fileOps;
    private long? _nextBeforeId;
    private readonly List<long?> _pageCursors = new() { null };
    private int _pageIndex;

    public override string Title => Strings.JournalTitle;

    public ObservableCollection<JournalItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _pageStatus = "";
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _hasPrevious;

    public JournalViewModel(OperationLogRepository log, IFileOpsService fileOps)
    {
        _log = log;
        _fileOps = fileOps;
    }

    public override void OnNavigatedTo() => _ = LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => LoadPageAsync(reset: true);

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || _nextBeforeId is null) return;
        _pageIndex++;
        if (_pageCursors.Count <= _pageIndex) _pageCursors.Add(_nextBeforeId);
        else _pageCursors[_pageIndex] = _nextBeforeId;
        await LoadPageAsync(reset: false);
    }

    [RelayCommand]
    private async Task LoadPreviousAsync()
    {
        if (IsBusy || _pageIndex == 0) return;
        _pageIndex--;
        await LoadPageAsync(reset: false);
    }

    private async Task LoadPageAsync(bool reset)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = null;
        if (reset)
        {
            _pageIndex = 0;
            _pageCursors.Clear();
            _pageCursors.Add(null);
        }
        try
        {
            var page = await Task.Run(() => _log.GetPage(PageSize, _pageCursors[_pageIndex]));
            Items.Clear();
            foreach (var e in page.Items) Items.Add(new JournalItemViewModel(e));
            _nextBeforeId = page.NextBeforeId;
            long start = (long)_pageIndex * PageSize + 1;
            long end = Math.Min(page.TotalCount, start + Items.Count - 1);
            HasPrevious = _pageIndex > 0;
            HasMore = _nextBeforeId is not null && end < page.TotalCount;
            PageStatus = page.TotalCount == 0 ? "" : $"{start:N0}–{end:N0} из {page.TotalCount:N0}";
            Empty = page.TotalCount == 0 ? Strings.Nothing : null;
        }
        catch (Exception ex)
        {
            Status = "Не удалось загрузить историю. Данные не изменены — попробуйте ещё раз.";
            Deduplicator.Core.Util.AppLog.Error("Journal.Load", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync(JournalItemViewModel? item)
    {
        if (item is null || !item.CanRestore) return;
        // #24: восстановление может не удаться (исходный путь занят, файл в карантине недоступен) —
        // раньше это молчало, теперь показываем причину.
        var ok = await Task.Run(() => _fileOps.Restore(item.Model));
        Status = ok ? null : Strings.RestoreFailed;
        await LoadAsync();
    }
}
