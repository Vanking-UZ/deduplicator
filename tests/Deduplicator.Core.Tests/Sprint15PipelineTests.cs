using Deduplicator.Core.Engine;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Tests;

/// <summary>
/// Этапы 2.4 (конвейер чтение→декод) и 2.6 (губернатор ресурсов): байтовая эквивалентность
/// pHash/pixel-hash конвейера и прямого расчёта по пути, отсутствие зависаний на отмене,
/// чистые функции губернатора (EWMA, клэмп DOP, бюджет памяти).
/// </summary>
public class Sprint15PipelineTests : IDisposable
{
    private readonly string _dir;

    public Sprint15PipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_s15_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Градиентная картинка (стиль Sprint13ImagingTests): свой узор на каждый seed.</summary>
    private static Image<Rgba32> MakeImage(int w, int h, int seed = 0)
    {
        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 3 + seed), (byte)(y * 5 + seed * 7), (byte)((x ^ y) + seed), 255);
            }
        });
        return img;
    }

    /// <summary>
    /// Корпус: PNG разных размеров/узоров + повёрнутая копия + иконка 32×32 (префильтр → null)
    /// + битый файл (→ null). Возвращает пары (путь, запись каталога).
    /// </summary>
    private List<(string Path, FileRecord Record)> MakeCorpus(int gradients = 6)
    {
        var corpus = new List<(string, FileRecord)>();
        void Add(string name, Action<string> save)
        {
            var p = Path.Combine(_dir, name);
            save(p);
            corpus.Add((p, new FileRecord
            {
                Id = corpus.Count + 1, VolumeId = 1, RelPath = name, Name = name,
                Size = new FileInfo(p).Length, Mtime = DateTimeOffset.Now, Category = FileCategory.Image
            }));
        }

        for (int i = 0; i < gradients; i++)
        {
            int idx = i;
            Add($"img_{idx}.png", p =>
            {
                using var img = MakeImage(150 + idx * 37, 120 + idx * 21, idx);
                img.SaveAsPng(p);
            });
        }
        Add("rotated.png", p =>
        {
            using var img = MakeImage(200, 160);
            using var r = img.Clone(c => c.Rotate(90f));
            r.SaveAsPng(p);
        });
        Add("icon.png", p => { using var img = MakeImage(32, 32); img.SaveAsPng(p); }); // префильтр → null
        Add("broken.png", p => File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4, 5 }));    // битый → null
        return corpus;
    }

    private static ImagePipelineOptions PipelineOptions(
        List<(string Path, FileRecord Record)> corpus, PipelineBudget budget,
        bool pixelHash = false, Action<FileRecord, string>? onComputed = null,
        Action<CancellationToken>? waitGate = null)
    {
        var byRel = corpus.ToDictionary(c => c.Record.RelPath, c => c.Path);
        return new ImagePipelineOptions
        {
            PathFor = f => byRel[f.RelPath],
            ReadDopFor = _ => 2,
            Budget = budget,
            MaxVolumeParallelism = 1,
            ComputePixelHash = pixelHash,
            WaitGate = waitGate,
            OnComputed = onComputed
        };
    }

    // === (а) 2.4: конвейер даёт байтово ТЕ ЖЕ pHash/pixel-hash, что прямой расчёт по пути ===
    [Fact]
    public void Pipeline_hashes_are_bytewise_identical_to_direct_path_compute()
    {
        var corpus = MakeCorpus();
        var svc = new ImageMatchService(_dir, rotationInvariant: true);

        // Эталон: старый путь — прямой расчёт по пути файла.
        var expectedPHash = corpus.ToDictionary(c => c.Record.RelPath, c => svc.ComputePHash(c.Path));
        var expectedPixel = corpus.ToDictionary(c => c.Record.RelPath, c => svc.ComputePixelHash(c.Path));
        Assert.Contains(expectedPHash.Values, h => h is not null); // корпус не выродился в null
        Assert.Null(expectedPHash["icon.png"]);
        Assert.Null(expectedPHash["broken.png"]);

        int gateCalls = 0;
        ImagePerceptualPipeline.Run(
            corpus.Select(c => c.Record).ToList(), svc,
            PipelineOptions(corpus, new PipelineBudget(128L * 1024 * 1024, Environment.ProcessorCount),
                pixelHash: true, waitGate: _ => Interlocked.Increment(ref gateCalls)),
            CancellationToken.None);

        foreach (var (_, record) in corpus)
        {
            Assert.Equal(expectedPHash[record.RelPath], record.PHash);   // байт в байт, включая null
            Assert.Equal(expectedPixel[record.RelPath], record.PixelHash);
        }
        Assert.True(gateCalls >= corpus.Count); // гейт паузы дёргается на обоих концах конвейера
    }

    // === (б) 2.4: крошечный бюджет — backpressure и обход канала для «крупных» файлов, та же эквивалентность ===
    [Fact]
    public void Pipeline_with_tiny_budget_and_oversized_files_matches_direct_compute()
    {
        var corpus = MakeCorpus();
        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        var expected = corpus.ToDictionary(c => c.Record.RelPath, c => svc.ComputePHash(c.Path));

        // Бюджет = медианный размер файла: часть корпуса НЕ влезает в канал (старый путь с диска),
        // остальные проталкиваются через байтовый backpressure; декодеров 2 — как при нехватке RAM.
        var sizes = corpus.Select(c => c.Record.Size).OrderBy(s => s).ToList();
        var budget = new PipelineBudget(Math.Max(1, sizes[sizes.Count / 2]), 2);
        Assert.Contains(corpus, c => c.Record.Size > budget.ChannelBytes);  // есть «крупные» (обход канала)
        Assert.Contains(corpus, c => c.Record.Size <= budget.ChannelBytes); // есть влезающие (через канал)

        ImagePerceptualPipeline.Run(corpus.Select(c => c.Record).ToList(), svc,
            PipelineOptions(corpus, budget), CancellationToken.None);

        foreach (var (_, record) in corpus)
            Assert.Equal(expected[record.RelPath], record.PHash);
    }

    // === (в) 2.4: отмена посреди работы — конвейер не дедлочится, метод завершается с OCE ===
    [Fact]
    public async Task Pipeline_cancellation_mid_work_completes_without_deadlock()
    {
        var corpus = MakeCorpus(gradients: 24); // побольше работы, чтобы отмена пришлась на середину
        var svc = new ImageMatchService(_dir, rotationInvariant: true);

        // Тесный бюджет (два самых крупных файла) + один декодер: продюсеры гарантированно ждут
        // в backpressure, когда прилетает отмена — худший случай для зависания.
        long cap = corpus.Max(c => c.Record.Size) * 2;
        using var cts = new CancellationTokenSource();
        int done = 0;
        var task = Task.Run(() => ImagePerceptualPipeline.Run(
            corpus.Select(c => c.Record).ToList(), svc,
            PipelineOptions(corpus, new PipelineBudget(cap, 1),
                onComputed: (_, _) => { if (Interlocked.Increment(ref done) == 2) cts.Cancel(); }),
            cts.Token));

        // Метод обязан завершиться (канал закрыт, бюджет освобождён, декодеры дождались).
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(finished == task, "конвейер не завершился за 60 с после отмены — дедлок");
        var ex = await Record.ExceptionAsync(() => task);
        Assert.True(ex is OperationCanceledException, $"ожидали OperationCanceledException, получили: {ex}");
        Assert.True(done < corpus.Count, "отмена не прервала работу — все файлы досчитались");
    }

    // === (г) 2.4: перегрузки от ReadOnlyMemory дают тот же результат, что расчёт по пути ===
    [Fact]
    public void ComputePHash_from_memory_equals_compute_from_path()
    {
        var p = Path.Combine(_dir, "mem.png");
        using (var img = MakeImage(300, 200)) img.SaveAsPng(p);
        var svc = new ImageMatchService(_dir, rotationInvariant: true);
        var bytes = File.ReadAllBytes(p);

        Assert.NotNull(svc.ComputePHash(p));
        Assert.Equal(svc.ComputePHash(p), svc.ComputePHash(bytes));
        Assert.Equal(svc.ComputePixelHash(p), svc.ComputePixelHash(bytes));

        // Битое содержимое из памяти — null, как и по пути (пропуск, не исключение).
        Assert.Null(svc.ComputePHash(new byte[] { 1, 2, 3 }));
        Assert.Null(svc.ComputePixelHash(new byte[] { 1, 2, 3 }));
    }

    // === (д) 2.6: EWMA сходится к постоянной скорости ===
    [Fact]
    public void ThroughputEwma_converges_to_constant_speed()
    {
        var ewma = new ThroughputEwma(alpha: 0.3);
        Assert.Equal(0.0, ewma.Value); // замеров не было
        for (int i = 0; i < 50; i++) ewma.Update(100L * 1024 * 1024, 1.0); // стабильно 100 МБ/с
        Assert.InRange(ewma.Value, 99.9, 100.1);
    }

    // === (е) 2.6: EWMA сглаживает одиночный выброс ===
    [Fact]
    public void ThroughputEwma_smooths_single_outlier()
    {
        var ewma = new ThroughputEwma(alpha: 0.3);
        for (int i = 0; i < 50; i++) ewma.Update(100L * 1024 * 1024, 1.0);
        ewma.Update(10L * 1024 * 1024, 1.0); // один провал до 10 МБ/с (кэш ОС/мелкий файл)
        // Сглаживание: значение сдвинулось, но осталось ближе к 100, чем к 10 (0.3·10 + 0.7·100 = 73).
        Assert.InRange(ewma.Value, 60.0, 90.0);

        // Пустые/нулевые замеры игнорируются, значение не портят.
        double before = ewma.Value;
        ewma.Update(0, 1.0);
        ewma.Update(100, 0.0);
        Assert.Equal(before, ewma.Value);
    }

    // === (ж) 2.6: клэмп DOP чтения — медленный SSD/USB2, быстрый SSD, HDD ===
    [Fact]
    public void ClampDop_slow_media_fast_ssd_and_hdd()
    {
        // «SSD» с устойчивой скоростью ниже 45 МБ/с — на деле USB2/сеть → читаем в 1 поток.
        Assert.Equal(1, GovernorDecisions.ClampDop(8, 20.0, hasSeekPenalty: false));
        // Быстрый SSD — не трогаем.
        Assert.Equal(8, GovernorDecisions.ClampDop(8, 400.0, hasSeekPenalty: false));
        // HDD (seek penalty) — как был: план тома уже читает его последовательно.
        Assert.Equal(1, GovernorDecisions.ClampDop(1, 20.0, hasSeekPenalty: true));
        // Замеров ещё нет (0) — без изменений, не путать с «медленно».
        Assert.Equal(8, GovernorDecisions.ClampDop(8, 0.0, hasSeekPenalty: false));
    }

    // === (з) 2.6: бюджет канала — обычная и высокая загрузка памяти ===
    [Fact]
    public void ResourceGovernor_budget_normal_and_high_memory_load()
    {
        long gb8 = 8L * 1024 * 1024 * 1024;
        long threshold = gb8 * 9 / 10;

        var normal = ResourceGovernor.ComputeBudget(gb8, memoryLoad: gb8 / 2, threshold, processorCount: 8);
        Assert.Equal(ResourceGovernor.NormalChannelBytes, normal.ChannelBytes); // 128 МБ
        Assert.Equal(8, normal.DecoderDop);                                     // по декодеру на ядро

        var high = ResourceGovernor.ComputeBudget(gb8, memoryLoad: gb8 * 95 / 100, threshold, processorCount: 8);
        Assert.Equal(ResourceGovernor.ReducedChannelBytes, high.ChannelBytes);  // 32 МБ
        Assert.Equal(2, high.DecoderDop);                                       // декодеров 2

        // Страховка крошечной памяти: канал не больше четверти всей доступной.
        long mb256 = 256L * 1024 * 1024;
        var tiny = ResourceGovernor.ComputeBudget(mb256, memoryLoad: mb256 / 2, mb256 * 9 / 10, processorCount: 2);
        Assert.Equal(mb256 / 4, tiny.ChannelBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
