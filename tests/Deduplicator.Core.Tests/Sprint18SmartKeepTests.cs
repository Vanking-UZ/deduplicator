using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

/// <summary>
/// 2.10 «Умные правила хранения»: KeepScorer выбирает, какую копию ОСТАВИТЬ (предвыбор эталона).
/// Проверяем эвристики (папки-эталоны, «мусорные» папки, имена-копии, дата создания),
/// детерминизм при равном счёте и ветку Smart в RepresentativeSelector.Pick.
/// Политика удаления скорингом НЕ расширяется — здесь только выбор «что оставить».
/// </summary>
public class Sprint18SmartKeepTests
{
    private static readonly DateTimeOffset T0 = new(2023, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static KeepCandidate C(string relPath, DateTimeOffset? created = null,
        DateTimeOffset? mtime = null, bool reference = false, long size = 1000) =>
        new(relPath, System.IO.Path.GetFileName(relPath), created ?? T0, mtime ?? T0, size, reference);

    // (а) Камерное имя с суффиксом копии в Downloads проигрывает осмысленному имени в «Фото»
    //     при РАВНЫХ датах — решают только имя и папка.
    [Fact]
    public void Meaningful_name_in_photos_beats_camera_copy_in_downloads()
    {
        var group = new List<KeepCandidate>
        {
            C(@"Users\u\Downloads\IMG_1234 (1).jpg"),
            C(@"Users\u\Фото\Отпуск 2024.jpg"),
        };
        Assert.Equal(1, KeepScorer.PickIndex(group));
        Assert.True(KeepScorer.ScoreGroup(group)[1] > KeepScorer.ScoreGroup(group)[0]);
    }

    // (б) Файл в папке-эталоне выигрывает ВСЕГДА — даже если сам похож на копию в мусорной папке,
    //     а соперник — «идеальный» кандидат (осмысленное имя, старше по Created).
    [Fact]
    public void Reference_folder_always_wins()
    {
        var group = new List<KeepCandidate>
        {
            C(@"Архив\Семейный альбом 2020.jpg", created: T0.AddYears(-5)),
            C(@"Downloads\Temp\фото - копия (2).jpg", created: T0, reference: true),
        };
        Assert.Equal(1, KeepScorer.PickIndex(group));
    }

    // (в) Суффиксы копий распознаются; «(важная)» — не цифры в скобках — НЕ срабатывает.
    [Theory]
    [InlineData("x - копия.jpg", true)]
    [InlineData("x — копия.jpg", true)]        // em dash — тоже Проводник
    [InlineData("x - копия (2).jpg", true)]
    [InlineData("x (2).png", true)]
    [InlineData("x_1.jpg", true)]
    [InlineData("x copy.jpg", true)]
    [InlineData("x - Copy.jpg", true)]
    [InlineData("Глава (важная).docx", false)] // цифр в скобках нет — это не копия
    [InlineData("Отпуск 2024.jpg", false)]
    [InlineData("IMG_1234.jpg", false)]        // камерный номер, а не дубль-суффикс «_1»
    [InlineData("report_2024.docx", false)]    // 4 цифры — год, не дубль-суффикс
    public void Copy_suffixes_are_detected(string name, bool expected)
    {
        Assert.Equal(expected, KeepScorer.LooksLikeCopyName(name));
    }

    // Осмысленность имени: длинное не-автогенерированное имя против камерных/цифровых шаблонов.
    [Theory]
    [InlineData("Отпуск 2024.jpg", true)]
    [InlineData("Семейный альбом.png", true)]
    [InlineData("IMG_1234.jpg", false)]     // камерный шаблон
    [InlineData("DSC_5678.jpg", false)]
    [InlineData("фото (3).jpg", false)]     // коротко и с копия-суффиксом
    [InlineData("20240101_120000.jpg", false)] // только цифры и разделители
    [InlineData("x.jpg", false)]            // слишком коротко
    public void Meaningful_name_is_detected(string name, bool expected)
    {
        Assert.Equal(expected, KeepScorer.IsMeaningfulName(name));
    }

    // «Мусорные» папки: сравнение по СЕГМЕНТАМ пути, не подстрокой.
    [Theory]
    [InlineData(@"Users\u\Downloads\a.jpg", true)]
    [InlineData(@"Загрузки\a.jpg", true)]
    [InlineData(@"var/tmp/Cache/a.jpg", true)] // прямые слэши тоже
    [InlineData(@"Users\u\.trash\a.jpg", true)]
    [InlineData(@"MyDownloadsArchive\a.jpg", false)] // «Downloads» подстрокой — НЕ мусор
    [InlineData(@"Фото\Downloads.jpg", false)]       // имя файла — не папка
    public void Junk_folders_match_by_segment(string relPath, bool expected)
    {
        Assert.Equal(expected, KeepScorer.IsInJunkFolder(relPath));
    }

    // (г) Детерминизм при равном счёте: сначала старейший по Mtime, затем самый короткий путь.
    [Fact]
    public void Ties_break_by_oldest_then_shortest_path()
    {
        var older = C(@"a\Фотоальбом лето.jpg", mtime: T0.AddDays(-1));
        var newer = C(@"b\Фотоальбом лето.jpg", mtime: T0);
        Assert.Equal(0, KeepScorer.PickIndex(new[] { older, newer }));
        Assert.Equal(1, KeepScorer.PickIndex(new[] { newer, older })); // порядок на входе не важен

        var shortPath = C(@"a\Фотоальбом лето.jpg");
        var longPath = C(@"a\b\c\Фотоальбом лето.jpg");
        Assert.Equal(0, KeepScorer.PickIndex(new[] { shortPath, longPath }));
        Assert.Equal(1, KeepScorer.PickIndex(new[] { longPath, shortPath }));
    }

    // Более ранняя Created даёт мягкий бонус (оригинал обычно старше), при прочих равных.
    [Fact]
    public void Earlier_created_gets_soft_bonus()
    {
        var group = new List<KeepCandidate>
        {
            C(@"a\Фотоальбом лето.jpg", created: T0),
            C(@"b\Фотоальбом лето.jpg", created: T0.AddYears(-2)),
        };
        Assert.Equal(1, KeepScorer.PickIndex(group));
        var scores = KeepScorer.ScoreGroup(group);
        Assert.Equal(KeepScorer.CreatedBonusMax, scores[1] - scores[0], 5);
    }

    // (д) Ветка Smart в RepresentativeSelector.Pick на FileRecord-ах: и эвристики, и предикат
    //     «в папке-эталоне» (isPreferredKeep), который прокидывает ScanService.
    [Fact]
    public void Pick_smart_selects_expected_file_record()
    {
        var files = new List<FileRecord>
        {
            new() { Id = 1, RelPath = @"Downloads\IMG_1234 (1).jpg", Name = "IMG_1234 (1).jpg",
                    Mtime = T0, Created = T0 },
            new() { Id = 2, RelPath = @"Фото\Отпуск 2024.jpg", Name = "Отпуск 2024.jpg",
                    Mtime = T0, Created = T0 },
        };
        Assert.Equal(2, RepresentativeSelector.Pick(files, RepresentativeRule.Smart).Id);

        // Предикат папки-эталона перевешивает всё: «плохой» кандидат становится эталоном.
        Assert.Equal(1, RepresentativeSelector.Pick(files, RepresentativeRule.Smart,
            isPreferredKeep: f => f.Id == 1).Id);
    }

    // Причины для тултипа бейджа «Эталон» — по силе сигнала.
    [Fact]
    public void Explain_returns_reason_by_strongest_signal()
    {
        var reference = C(@"Фото\Отпуск 2024.jpg", reference: true);
        var copy = C(@"Downloads\Отпуск 2024 - копия.jpg");
        Assert.Equal(KeepScorer.ReasonReference, KeepScorer.Explain(new[] { reference, copy }, 0));

        var clean = C(@"Фото\Отпуск 2024.jpg");
        Assert.Equal(KeepScorer.ReasonNotACopy, KeepScorer.Explain(new[] { clean, copy }, 0));

        var meaningful = C(@"a\Семейный альбом.jpg");
        var camera = C(@"b\IMG_1234.jpg");
        Assert.Equal(KeepScorer.ReasonMeaningfulName, KeepScorer.Explain(new[] { meaningful, camera }, 0));

        var older = C(@"a\IMG_1111.jpg", mtime: T0.AddDays(-1));
        var newer = C(@"b\IMG_1111.jpg", mtime: T0);
        Assert.Equal(KeepScorer.ReasonOldest, KeepScorer.Explain(new[] { older, newer }, 0));
    }

    // Smart не ломает выбор для группы из одного файла (вырожденный, но реальный случай hardlink-схлопывания).
    [Fact]
    public void Single_candidate_group_is_handled()
    {
        var one = new List<FileRecord>
        {
            new() { Id = 7, RelPath = @"Downloads\x (2).jpg", Name = "x (2).jpg", Mtime = T0, Created = T0 },
        };
        Assert.Equal(7, RepresentativeSelector.Pick(one, RepresentativeRule.Smart).Id);
    }
}
