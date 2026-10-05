using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

[CollectionDefinition(AppLogSensitiveCollection.Name, DisableParallelization = true)]
public sealed class AppLogSensitiveCollection
{
    public const string Name = "AppLog-sensitive tests";
}

/// <summary>
/// Сохранение прошлых завершённых результатов при отмене/сбое,
/// scope-aware перцептивные запросы, постраничная выдача похожих групп, гарантия различения
/// «тот же размер — другое содержимое» полным хэшем (основа ревалидации удаления P0.1).
/// </summary>
[Collection(AppLogSensitiveCollection.Name)]
public class Sprint11Projact7Tests : IDisposable
{
    private readonly string _dir;

    public Sprint11Projact7Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s11_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private static FileRecord Img(long volId, int i, byte[]? phash = null) => new()
    {
        VolumeId = volId, RelPath = $"img{i}.jpg", Name = $"img{i}.jpg", Ext = ".jpg",
        Size = 1000 + i, Mtime = DateTimeOffset.Now, Category = FileCategory.Image,
        PHash = phash, ScanState = phash is null ? ScanState.Meta : ScanState.Perceptual,
        // Заполненный pHash — текущей версии алгоритма, иначе строка снова попадёт в пересчёт (этап 2.2).
        PHashVer = phash is null ? 1 : Deduplicator.Core.Imaging.ImageMatchService.CurrentPHashVersion
    };

    // === P0: пересборка групп в идущем скане НЕ стирает результаты другой (завершённой) сессии ===
    [Fact]
    public void Exact_grouping_does_not_wipe_other_sessions_groups()
    {
        var db = new Database(Path.Combine(_dir, "preserve.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var hash = new HashService();
        var sessions = new ScanSessionRepository(db);
        var vol = new VolumeRecord { Serial = "P7A", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        long sessionA = sessions.Create("{}");
        sessions.Finish(sessionA, ScanStage.Completed);
        long sessionB = sessions.Create("{}");

        // Завершённая сессия A: своя точная группа из файлов с РАЗНЫМИ хэшами (воронка B их не поднимет).
        var aFiles = new List<FileRecord>
        {
            new() { VolumeId = vol.Id, RelPath = "a1.bin", Name = "a1.bin", Size = 11, Mtime = DateTimeOffset.Now,
                    Category = FileCategory.Other, FullHash = new byte[] { 1 }, ScanState = ScanState.Full },
            new() { VolumeId = vol.Id, RelPath = "a2.bin", Name = "a2.bin", Size = 12, Mtime = DateTimeOffset.Now,
                    Category = FileCategory.Other, FullHash = new byte[] { 2 }, ScanState = ScanState.Full },
        };
        files.UpsertMetaBatch(aFiles);
        groups.InsertGroups(new[]
        {
            new DuplicateGroup
            {
                MatchType = MatchType.Exact, Confidence = 1.0, RepresentativeId = aFiles[0].Id, ReclaimableBytes = 12,
                Members = { new DuplicateMember { FileId = aFiles[0].Id }, new DuplicateMember { FileId = aFiles[1].Id } }
            }
        }, sessionA);

        // Сессия B: два байт-идентичных файла на диске → воронка построит группу для B.
        var data = new byte[20_000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(_dir, "b1.bin"), data);
        File.WriteAllBytes(Path.Combine(_dir, "b2.bin"), (byte[])data.Clone());
        var full = hash.ComputeFullHash(Path.Combine(_dir, "b1.bin"));
        files.UpsertMetaBatch(new List<FileRecord>
        {
            new() { VolumeId = vol.Id, RelPath = "b1.bin", Name = "b1.bin", Size = data.Length, Mtime = DateTimeOffset.Now,
                    Category = FileCategory.Other, FullHash = full, ScanState = ScanState.Full },
            new() { VolumeId = vol.Id, RelPath = "b2.bin", Name = "b2.bin", Size = data.Length, Mtime = DateTimeOffset.Now,
                    Category = FileCategory.Other, FullHash = full, ScanState = ScanState.Full },
        });

        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var dict = new Dictionary<long, VolumeRecord> { [vol.Id] = vol };
        int created = grouper.BuildExactGroups(dict, _ => true, RepresentativeRule.Oldest, null, default, sessionId: sessionB);

        Assert.Equal(1, created);                                    // построена одна группа (для B)
        Assert.Single(groups.GetGroups(MatchType.Exact, sessionA));  // ГРУППА СЕССИИ A НЕ СТЁРТА (был баг глоб. clear)
        Assert.Single(groups.GetGroups(MatchType.Exact, sessionB));  // и группа B на месте
    }

    // === P0: реальный сбойный скан НЕ удаляет результаты прошлого завершённого скана ===
    [Fact]
    public async Task Failed_scan_preserves_previous_completed_results()
    {
        var corpus = Path.Combine(_dir, "corpus");
        Directory.CreateDirectory(corpus);
        var data = new byte[40_000];
        new Random(3).NextBytes(data);
        File.WriteAllBytes(Path.Combine(corpus, "dup1.bin"), data);
        File.WriteAllBytes(Path.Combine(corpus, "dup2.bin"), (byte[])data.Clone());

        var db = new Database(Path.Combine(_dir, "e2e.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var lsh = new LshRepository(db);
        var sessions = new ScanSessionRepository(db);
        var settings = new SettingsService(new AppPaths(Path.Combine(_dir, "app")));
        var volSvc = new VolumeService(volumes);
        var hash = new HashService();
        var images = new ImageMatchService(Path.Combine(_dir, "thumbs"), rotationInvariant: false);
        var audio = new AudioMatchService(null);
        var grouper = new DuplicateGrouper(files, groups, lsh, hash);
        var scan = new ScanService(volumes, files, sessions, volSvc, hash, images, audio, grouper, settings,
            new FastDedup.Engine.FastFileLister(), new VolumeScanStateRepository(db));

        // Скан A: успешно находит точную пару.
        await scan.RunAsync(new ScanOptions { Folders = { corpus }, Depth = ScanDepth.ExactOnly, Parallelism = 2 }, default);
        Assert.Equal(ScanStage.Completed, scan.Progress.Stage);
        long? completedA = sessions.GetLatestCompletedSessionId();
        Assert.NotNull(completedA);
        Assert.Single(groups.GetGroups(MatchType.Exact, completedA));

        // Скан B: пустой набор категорий → валидация падает (детерминированный сбой) ПОСЛЕ создания сессии B.
        await scan.RunAsync(new ScanOptions
        {
            Folders = { corpus }, Depth = ScanDepth.ExactOnly, Parallelism = 2,
            IncludedCategories = new HashSet<FileCategory>()
        }, default);
        Assert.Equal(ScanStage.Failed, scan.Progress.Stage);

        // Прошлый завершённый результат A СОХРАНЁН (раньше finally глобально чистил группы не-текущей сессии).
        Assert.Equal(completedA, sessions.GetLatestCompletedSessionId());
        Assert.Single(groups.GetGroups(MatchType.Exact, completedA));
    }

    // === P1: перцептивные запросы учитывают область сессии (scan_scope_files) ===
    [Fact]
    public void Perceptual_queries_are_scope_aware()
    {
        var db = new Database(Path.Combine(_dir, "scope.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var sessions = new ScanSessionRepository(db);
        var vol = new VolumeRecord { Serial = "P7S", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        // 10 изображений ВНЕ области (ждут pHash) + 2 в области сессии S.
        var outside = Enumerable.Range(0, 10).Select(i => Img(vol.Id, i)).ToList();
        files.UpsertMetaBatch(outside);
        var inScope = new List<FileRecord> { Img(vol.Id, 100), Img(vol.Id, 101) };
        files.UpsertMetaBatch(inScope);

        long s = sessions.Create("{}");
        files.AddScope(s, inScope.Select(f => f.Id).ToList());

        // Глобально — все 12 ждут pHash; в области — только 2.
        Assert.Equal(12, files.CountNeedingPerceptual(FileCategory.Image));
        Assert.Equal(2, files.CountNeedingPerceptual(FileCategory.Image, s));

        var page = files.GetNeedingPerceptualPage(FileCategory.Image, 0, 100, s);
        Assert.Equal(2, page.Count);
        Assert.True(page.All(f => inScope.Any(x => x.Id == f.Id)));   // только файлы области

        // Теперь проставим pHash всем — проверим GetByCategoryWithPerceptual со scope.
        // Версия — текущая: со старой (v1) строки остались бы в выборке needing-perceptual (этап 2.2).
        foreach (var f in outside.Concat(inScope))
        {
            f.PHash = new byte[] { 9 };
            f.PHashVer = Deduplicator.Core.Imaging.ImageMatchService.CurrentPHashVersion;
        }
        files.UpdateComputedBatch(outside.Concat(inScope).ToList());
        Assert.Equal(12, files.GetByCategoryWithPerceptual(FileCategory.Image).Count);       // глобально
        Assert.Equal(2, files.GetByCategoryWithPerceptual(FileCategory.Image, s).Count);     // только область
        Assert.Equal(0, files.CountNeedingPerceptual(FileCategory.Image, s));                // в области уже все с pHash

        // sessionId без области → глобальное поведение (страховка: пустой scope не означает пустой результат).
        long empty = sessions.Create("{}");
        Assert.Equal(12, files.GetByCategoryWithPerceptual(FileCategory.Image, empty).Count);
    }

    // === P1: постраничная выдача похожих групп (перегрузки на один тип) ===
    [Fact]
    public void Similar_group_paging_single_type_overloads()
    {
        var db = new Database(Path.Combine(_dir, "paging.db"));
        db.Initialize();
        var volumes = new VolumeRepository(db);
        var files = new FileRepository(db);
        var groups = new GroupRepository(db);
        var sessions = new ScanSessionRepository(db);
        var vol = new VolumeRecord { Serial = "P7P", MountPath = _dir, IsConnected = true };
        vol.Id = volumes.Upsert(vol);

        var recs = Enumerable.Range(0, 12).Select(i => Img(vol.Id, i, phash: new byte[] { (byte)i })).ToList();
        files.UpsertMetaBatch(recs);

        long sid = sessions.Create("{}");
        var groupList = new List<DuplicateGroup>();
        for (int i = 0; i < 6; i++)
            groupList.Add(new DuplicateGroup
            {
                MatchType = MatchType.SimilarImage, Confidence = 1.0, RepresentativeId = recs[i * 2].Id,
                ReclaimableBytes = 1000 + i,
                Members =
                {
                    new DuplicateMember { FileId = recs[i * 2].Id, Similarity = 1.0 },
                    new DuplicateMember { FileId = recs[i * 2 + 1].Id, Similarity = 0.95 }
                }
            });
        groups.InsertGroups(groupList, sid);

        Assert.Equal(6, groups.GetGroupCount(MatchType.SimilarImage, sid));
        Assert.Equal(0, groups.GetGroupCount(MatchType.SimilarAudio, sid));

        var p1 = groups.GetGroupsPage(MatchType.SimilarImage, sid, 0, 4);
        var p2 = groups.GetGroupsPage(MatchType.SimilarImage, sid, 4, 4);
        Assert.Equal(4, p1.Count);
        Assert.Equal(2, p2.Count);
        Assert.All(p1, g => Assert.Equal(2, g.Members.Count));                 // члены подгружены
        // Без перекрытий/пропусков: 6 уникальных групп на двух страницах.
        Assert.Equal(6, p1.Select(g => g.Id).Concat(p2.Select(g => g.Id)).Distinct().Count());
    }

    // === P0.1: основа ревалидации удаления — «тот же размер, другое содержимое» различается полным хэшем ===
    [Fact]
    public void Full_hash_distinguishes_same_size_different_content()
    {
        var hash = new HashService();
        var a = Path.Combine(_dir, "same1.bin");
        var b = Path.Combine(_dir, "same2.bin");
        var bytes = new byte[50_000];
        new Random(5).NextBytes(bytes);
        File.WriteAllBytes(a, bytes);

        var ha1 = hash.ComputeFullHash(a);

        // Перезаписываем тем же РАЗМЕРОМ, но другим содержимым (сценарий «подменили файл после скана»).
        var other = (byte[])bytes.Clone();
        other[12345] ^= 0xFF;
        File.WriteAllBytes(b, other);
        Assert.Equal(new FileInfo(a).Length, new FileInfo(b).Length);          // размеры равны

        var ha2 = hash.ComputeFullHash(a);
        var hb = hash.ComputeFullHash(b);
        Assert.Equal(ha1, ha2);                                                 // тот же файл — тот же хэш
        Assert.NotEqual(ha1, hb);                                               // тот же размер, другое содержимое → ХЭШ ДРУГОЙ
    }

    // === P0.2a: диагностическая строка движка называет реальный конвейер ExactDuplicateEngineV2 ===
    [Fact]
    public void EngineBuildInfo_summary_identifies_v2_engine()
    {
        var s = Deduplicator.Core.Engine.EngineBuildInfo.Summary;
        Assert.StartsWith("ExactDuplicateEngineV2", s);
        Assert.Contains("IndexFormat v", s);
        Assert.Contains("Core ", s);
        Assert.Contains("FastDedup.Engine ", s);
    }

    // === P0.2b: лог пишется в UTF-8 с BOM и корректно читает кириллические пути (без кракозябр) ===
    [Fact]
    public void AppLog_writes_utf8_bom_and_reads_cyrillic()
    {
        var path = Path.Combine(_dir, "logs", "deduplicator.log");
        Deduplicator.Core.Util.AppLog.Configure(path);
        var marker = "Путь C:\\Users\\Дмитрий\\тест_" + Guid.NewGuid().ToString("N");
        Deduplicator.Core.Util.AppLog.Write(marker);

        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // UTF-8 BOM
        Assert.Contains(marker, File.ReadAllText(path, System.Text.Encoding.UTF8));                  // кириллица читается верно

        // Каталог логов удалён после старта — следующая запись восстанавливает его (не падает).
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        Deduplicator.Core.Util.AppLog.Write("после удаления каталога");
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
