using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Группа дубликатов с членами и логикой отметок (гарантия «оставить хотя бы одну», FR-29).</summary>
public partial class GroupItemViewModel : ObservableObject
{
    public DuplicateGroup Model { get; }
    public ObservableCollection<FileItemViewModel> Files { get; } = new();

    [ObservableProperty] private string? _warning;

    private bool _guard;

    public GroupItemViewModel(DuplicateGroup model, IEnumerable<FileItemViewModel> files)
    {
        Model = model;
        foreach (var f in files)
        {
            f.MarkChanged += OnMarkChanged;
            Files.Add(f);
        }
    }

    public bool IsSimilar => Model.MatchType is MatchType.SimilarImage or MatchType.SimilarAudio;
    public bool HasImages => Model.MatchType is MatchType.SimilarImage or MatchType.Exact;
    public bool IsExact => Model.MatchType is MatchType.Exact or MatchType.ExactVideo;

    /// <summary>Группа состоит из фотографий → показываем крупной галереей (наглядное сравнение),
    /// иначе — компактным списком строк. Члены группы одной категории (одинаковый/похожий контент),
    /// поэтому достаточно посмотреть на первый файл.</summary>
    public bool IsImageGroup => Files.Count > 0 && Files[0].Model.Category == FileCategory.Image;
    public bool IsFileGroup => !IsImageGroup;

    /// <summary>Иконка-индикатор типа группы в заголовке карточки.</summary>
    public Geometry? GroupIcon => Converters.Converters.Icon.Convert(
        Model.MatchType switch
        {
            MatchType.SimilarImage => "IconImage",
            MatchType.SimilarAudio => "IconMusic",
            MatchType.ExactVideo => "IconVideo",
            _ => "IconLayers"
        }, typeof(Geometry), null, CultureInfo.CurrentCulture) as Geometry;

    /// <summary>Точная группа, не сверенная полностью (есть офлайн/непрочитанные члены) — удаление запрещено.</summary>
    public bool IsUnverifiedExact => IsExact && Model.Confidence < 1.0;

    public string CountText => $"{Strings.GroupOf}: {Files.Count}";

    // P0.3: у похожих не показываем «можно освободить» как рекомендацию к удалению.
    // P0.2: непроверенная точная группа сообщает, что удаление отключено.
    public string ReclaimText => IsSimilar
        ? "Похожие — проверьте вручную"
        : IsUnverifiedExact
            ? "Не проверено полностью — удаление отключено"
            : $"{Strings.Reclaimable}: {Format.Bytes(Model.ReclaimableBytes)}";

    public bool ShowUnverifiedWarning => IsUnverifiedExact;
    /// <summary>Пакетные действия (отметить все кроме эталона) — только для проверенных точных групп.</summary>
    public bool ShowBatchActions => !IsSimilar && !IsUnverifiedExact;
    public string UnverifiedWarningText =>
        "Эта группа проверена не полностью (часть файлов недоступна). Подключите все диски и запустите " +
        "проверку ещё раз. Удаление из этой группы отключено.";

    public string ConfidenceText => Format.Percent(Model.Confidence);
    public bool ShowConfidence => IsSimilar;

    public int MarkedCount => Files.Count(f => f.IsMarked);
    public bool HasMarked => MarkedCount > 0;

    /// <summary>Максимальная схожесть среди не-эталонных членов (для фильтра по порогу).</summary>
    public double TopSimilarity => Files.Where(f => !f.IsRepresentative)
        .Select(f => f.Similarity).DefaultIfEmpty(1.0).Max();

    [RelayCommand]
    private void MarkAllButRepresentative()
    {
        foreach (var f in Files)
            f.IsMarked = !f.IsRepresentative && !f.IsProtected;
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var f in Files) f.IsMarked = false;
        Warning = null;
    }

    private void OnMarkChanged(FileItemViewModel f)
    {
        if (_guard) return;
        // Никогда не оставляем группу без хотя бы одной ДОСТУПНОЙ копии: блокируем отметку, если после
        // неё не останется ни одного неотмеченного файла с реальным путём (FR-29). Это закрывает случай
        // «эталон на отключённом диске» — иначе можно было удалить все доступные копии.
        if (f.IsMarked && !Files.Any(x => !x.IsMarked && x.FullPath is not null))
        {
            _guard = true;
            f.IsMarked = false;
            _guard = false;
            Warning = Strings.KeepAtLeastOne;
        }
        else
        {
            Warning = null;
        }
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(HasMarked));
    }

    public IEnumerable<FileItemViewModel> MarkedFiles => Files.Where(f => f.IsMarked);
}
