using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Один файл внутри группы дубликатов.</summary>
public partial class FileItemViewModel : ObservableObject
{
    private readonly IImageMatchService _thumbs;

    public FileRecord Model { get; }
    public bool IsRepresentative { get; }
    public double Similarity { get; }
    public string? FullPath { get; }
    public string? VolumeLabel { get; }

    /// <summary>Файл в папке-эталоне — защищён от удаления (FR-подобно dupeGuru).</summary>
    public bool IsProtected { get; }

    /// <summary>
    /// 2.10: причина «почему выбран эталоном» — тултип бейджа «Эталон». Заполняется только у
    /// эталона; вычисляется при построении карточки (в БД не хранится). null — тултипа нет.
    /// </summary>
    public string? RepresentativeReason { get; }

    /// <summary>
    /// 3.7: самый резкий кадр в группе ПОХОЖИХ фото (серийная съёмка). Вычисляется в
    /// ResultsViewModel.Build: только для MatchType.SimilarImage, только когда резкость есть
    /// у ≥2 членов и разница заметна (иначе шум — бейдж не показываем). Чистая подсказка:
    /// на CanMark/удаление не влияет.
    /// </summary>
    public bool IsSharpest { get; }

    /// <summary>Текст бейджа «Лучший кадр» (null — бейджа нет).</summary>
    public string? SharpnessBadgeText => IsSharpest ? Strings.SharpestBadge : null;

    /// <summary>Разрешает ли ГРУППА удаление (точная сверенная — да; hash-only/похожие — по правилам).</summary>
    public bool GroupAllowsDeletion { get; }

    /// <summary>
    /// Можно ли отметить файл к удалению. Запрещаем, если: группа не разрешает удаление
    /// (hash-only exact, confidence&lt;1.0), файл защищён, или файл сейчас недоступен (нет пути).
    /// </summary>
    public bool CanMark => GroupAllowsDeletion && !IsProtected && FullPath is not null;

    public event Action<FileItemViewModel>? MarkChanged;

    [ObservableProperty] private bool _isMarked;
    [ObservableProperty] private Bitmap? _thumbnail;

    public FileItemViewModel(FileRecord model, double similarity, bool isRepresentative,
        string? fullPath, string? volumeLabel, IImageMatchService thumbs, bool isProtected = false,
        bool groupAllowsDeletion = true, bool loadThumbnail = true, string? representativeReason = null,
        bool isSharpest = false)
    {
        Model = model;
        Similarity = similarity;
        IsRepresentative = isRepresentative;
        FullPath = fullPath;
        VolumeLabel = volumeLabel;
        IsProtected = isProtected;
        GroupAllowsDeletion = groupAllowsDeletion;
        RepresentativeReason = representativeReason;
        IsSharpest = isSharpest;
        _thumbs = thumbs;

        // loadThumbnail=false — VM строится только ради галочек/экспорта («Выбрать все», CSV):
        // жадное декодирование превью для КАЖДОГО изображения давало гигабайты Bitmap-ов
        // и OOM на слабых ПК.
        if (loadThumbnail && Model.Category == FileCategory.Image && FullPath is not null)
            _ = LoadThumbnailAsync();
    }

    public string Name => Model.Name;
    public string RelPath => Model.RelPath;
    public string SizeText => Format.Bytes(Model.Size);
    public string VolumeText => VolumeLabel ?? $"Диск №{Model.VolumeId}";
    public string SimilarityText => Format.Percent(Similarity);
    public bool ShowSimilarity => !IsRepresentative && Similarity < 1.0;
    public string ModifiedText => Format.DateShort(Model.Mtime);
    public bool IsAvailable => FullPath is not null;

    /// <summary>Путь для просмотрщика сравнения: полный, а для офлайн-тома — метка тома + относительный.</summary>
    public string DisplayPath => FullPath ?? $"{VolumeText} · {RelPath}";

    public Geometry? CategoryIcon => Converters.Converters.CategoryGeometry.Convert(
        Model.Category, typeof(Geometry), null, System.Globalization.CultureInfo.CurrentCulture) as Geometry;

    private bool _markGuard;
    partial void OnIsMarkedChanged(bool value)
    {
        if (_markGuard) return;
        if (value && !CanMark) // защищённые / hash-only / недоступные файлы отмечать нельзя
        {
            _markGuard = true;
            IsMarked = false;
            _markGuard = false;
            return;
        }
        MarkChanged?.Invoke(this);
    }

    // Ограничиваем число одновременных генераций миниатюр, чтобы не топить пул потоков
    // и не вызывать лаги при открытии результатов с сотнями превью. Половина ядер (2–8):
    // фиксированные 3 недоиспользовали 8-ядерные машины и душили 2-ядерные (этап 1.11).
    private static readonly SemaphoreSlim ThumbGate = CreateThumbGate();
    private static SemaphoreSlim CreateThumbGate()
    {
        int slots = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        return new SemaphoreSlim(slots, slots);
    }

    private volatile bool _thumbnailDisposed;

    /// <summary>
    /// Освобождает нативную память миниатюры. Вызывается при очистке коллекций результатов:
    /// Bitmap-ы держат Skia-память вне управляемой кучи, и без явного Dispose она копилась
    /// до OOM при повторных загрузках результатов (B5).
    /// </summary>
    public void DisposeThumbnail()
    {
        _thumbnailDisposed = true;
        var t = Thumbnail;
        Thumbnail = null;
        t?.Dispose();
    }

    private async Task LoadThumbnailAsync()
    {
        await ThumbGate.WaitAsync();
        try
        {
            if (_thumbnailDisposed) return; // коллекцию уже очистили — не декодируем впустую

            var key = Model.FullHash is not null
                ? Convert.ToHexString(Model.FullHash)
                : (Model.PHash is not null ? Convert.ToHexString(Model.PHash) : "id" + Model.Id);

            // 320 px: достаточно чётко для крупной галереи результатов (плитка ~150 px, в т.ч. на HiDPI).
            var path = await Task.Run(() => _thumbs.EnsureThumbnail(FullPath!, key, 320));
            if (path is null || !File.Exists(path)) return;

            var bmp = await Task.Run(() => new Bitmap(path));
            if (_thumbnailDisposed) { bmp.Dispose(); return; }
            Thumbnail = bmp;
        }
        catch { /* миниатюра необязательна */ }
        finally { ThumbGate.Release(); }
    }
}
