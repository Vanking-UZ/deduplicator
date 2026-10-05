using System.Threading.Channels;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Engine;

/// <summary>Параметры конвейера чтение→декод (этап 2.4).</summary>
public sealed class ImagePipelineOptions
{
    /// <summary>Полный путь к файлу; null — том не смонтирован, файл молча пропускается.</summary>
    public required Func<FileRecord, string?> PathFor { get; init; }

    /// <summary>DOP чтения для тома. Вызывается исполнителем один раз на страницу —
    /// сюда подаётся план тома с клэмпом губернатора (2.6).</summary>
    public required Func<long, int> ReadDopFor { get; init; }

    /// <summary>Runtime-only physical-device key тома; null включает безопасный fallback по volume id.</summary>
    public Func<long, string?>? PhysicalDeviceKeyFor { get; init; }

    /// <summary>Последовательный ли план тома (HDD/медленный съёмный/сеть).</summary>
    public Func<long, bool>? PreferSequentialFor { get; init; }

    /// <summary>Бюджет канала (суммарные байты «в полёте») и число декодеров — от губернатора (2.6).</summary>
    public required PipelineBudget Budget { get; init; }

    /// <summary>Сколько томов читать одновременно (как в exact-движке/старом ComputeParallel).</summary>
    public int MaxVolumeParallelism { get; init; } = 1;

    /// <summary>Считать ли pixel-hash (настройка «одинаковые пиксели, разные метаданные»).</summary>
    public bool ComputePixelHash { get; init; }

    /// <summary>Гейт паузы (ScanService.WaitGate): дёргается на ОБОИХ концах конвейера.</summary>
    public Action<CancellationToken>? WaitGate { get; init; }

    /// <summary>Расчёт по файлу завершён (в т.ч. с null-результатом — иконка/битый) — прогресс/версия pHash.</summary>
    public Action<FileRecord, string>? OnComputed { get; init; }

    /// <summary>Ошибка чтения/декода одного файла — файл пропущен, конвейер продолжает.</summary>
    public Action? OnSkipped { get; init; }

    /// <summary>Замер чтения продюсером: (volumeId, bytes, elapsedSeconds) — пища для EWMA (2.6).</summary>
    public Action<long, long, double>? OnReadMeasured { get; init; }
}

/// <summary>
/// Конвейер чтение→декод для перцептивного этапа (2.4). Раньше чтение с диска и декод происходили
/// в одном воркере с DOP тома: на HDD (ContentDop=1) одно ядро декодировало, диск простаивал,
/// остальные ядра тоже. Теперь ПРОДЮСЕРЫ читают файлы целиком в память по плану тома (диск — со
/// своим DOP, на HDD последовательно), а ДЕКОДЕРЫ (пул по бюджету губернатора) считают
/// pHash/pixel-hash ИЗ ПАМЯТИ. Backpressure — бюджет по СУММАРНЫМ байтам в полёте (~128 МБ):
/// продюсер ждёт, пока декодеры освободят место. Семантика хэшей не меняется — байт в байт
/// те же значения, что при расчёте по пути (общий core в ImageMatchService).
/// </summary>
public static class ImagePerceptualPipeline
{
    private readonly record struct WorkItem(FileRecord File, string Path, byte[] Content);

    public static void Run(IReadOnlyCollection<FileRecord> files, IImageMatchService images,
        ImagePipelineOptions opts, CancellationToken ct)
    {
        if (files.Count == 0) return;

        var budget = new ByteBudget(opts.Budget.ChannelBytes);
        // Канал без счётного лимита: реальный ограничитель — байтовый бюджет, а страница
        // перцептива ≤ 500 файлов, так что число элементов и так мало. Синхронным продюсерам
        // не нужны блокирующие Write — TryWrite в незакрытый unbounded-канал не отказывает.
        var channel = Channel.CreateUnbounded<WorkItem>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        int decoderDop = Math.Max(1, opts.Budget.DecoderDop);
        var decoders = new Task[decoderDop];
        for (int i = 0; i < decoderDop; i++)
            decoders[i] = Task.Run(() => DecodeLoopAsync(channel.Reader, images, opts, budget, ct),
                CancellationToken.None);

        Exception? producersFailed = null;
        try
        {
            VolumeParallelExecutor.RunByVolume(files, f => f.VolumeId, opts.ReadDopFor,
                vid => opts.PhysicalDeviceKeyFor?.Invoke(vid) ?? $"volume:{vid}",
                vid => opts.PreferSequentialFor?.Invoke(vid) ?? false,
                opts.MaxVolumeParallelism, ct,
                f => Produce(f, images, opts, budget, channel.Writer, ct));
        }
        catch (Exception ex)
        {
            // Канал обязан закрыться и декодеры — дожаться при ЛЮБОМ исходе (иначе повисшие
            // Task с занятым бюджетом), поэтому не rethrow сразу, а после WaitAll ниже.
            producersFailed = ex;
        }

        channel.Writer.TryComplete();
        try { Task.WaitAll(decoders); }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Отмена декодеров: наружу уйдёт единый OCE ниже.
        }

        // Отмена приоритетнее деталей отказа: наружу единый OCE (RunCore переведёт сессию в Cancelled).
        ct.ThrowIfCancellationRequested();
        if (producersFailed is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producersFailed).Throw();
    }

    /// <summary>
    /// Продюсер: облачный guard по атрибутам ДО чтения, файл целиком в память под байтовым
    /// бюджетом, замер скорости для EWMA. Плейсхолдеры и файлы крупнее бюджета (редкие TIFF/PSD)
    /// идут старым путём — по пути с диска, не раздувая канал. Ошибка чтения одного файла —
    /// пропуск, не падение конвейера.
    /// </summary>
    private static void Produce(FileRecord f, IImageMatchService images, ImagePipelineOptions opts,
        ByteBudget budget, ChannelWriter<WorkItem> writer, CancellationToken ct)
    {
        opts.WaitGate?.Invoke(ct);
        var path = opts.PathFor(f);
        if (path is null) return;
        var fixedPath = LongPath.Fix(path);
        if (!File.Exists(fixedPath)) return;
        try
        {
            // Плейсхолдер OneDrive: содержимое НЕ читаем (иначе гидратация из облака, B4).
            // Путь по строке отработает как раньше — вернёт null-хэши, файл засчитается прогрессом.
            if (CloudFiles.IsPlaceholderPath(path))
            {
                ComputeFromPath(f, path, images, opts, ct);
                return;
            }

            byte[]? content = null;
            long len;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(fixedPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                len = fs.Length;
                if (len > 0 && len <= opts.Budget.ChannelBytes)
                {
                    budget.Acquire(len, ct); // ДО чтения: backpressure по суммарным байтам в полёте
                    try
                    {
                        var buf = new byte[len];
                        int read = 0;
                        while (read < buf.Length)
                        {
                            int n = fs.Read(buf, read, buf.Length - read);
                            if (n == 0) break;
                            read += n;
                        }
                        // Файл усох между stat и чтением — обрезанный буфер из памяти не декодируем,
                        // отдаём старому пути ниже: он увидит актуальное содержимое.
                        if (read == buf.Length) content = buf;
                        else budget.Release(len);
                    }
                    catch
                    {
                        budget.Release(len);
                        throw;
                    }
                }
            }
            sw.Stop();

            if (content is null)
            {
                // Нулевой/усохший или крупнее бюджета канала — старый путь с диска.
                ComputeFromPath(f, path, images, opts, ct);
                return;
            }

            opts.OnReadMeasured?.Invoke(f.VolumeId, len, sw.Elapsed.TotalSeconds);
            if (!writer.TryWrite(new WorkItem(f, path, content)))
            {
                // Канал закрыт — только при сворачивании конвейера (отмена).
                budget.Release(len);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { opts.OnSkipped?.Invoke(); } // ошибка чтения одного файла — пропуск (NFR-1)
    }

    /// <summary>Старый путь (2.2): расчёт по пути с диска — для плейсхолдеров и файлов крупнее бюджета.
    /// 3.7: pHash и резкость — за один декод (ComputeSignatures), второго прохода по диску нет.</summary>
    private static void ComputeFromPath(FileRecord f, string path, IImageMatchService images,
        ImagePipelineOptions opts, CancellationToken ct)
    {
        var sig = images.ComputeSignatures(path, ct);
        f.PHash = sig.PHash;
        f.Sharpness = sig.Sharpness;
        if (opts.ComputePixelHash) f.PixelHash = images.ComputePixelHash(path, ct);
        opts.OnComputed?.Invoke(f, path);
    }

    /// <summary>Декодер: берёт содержимое из канала, считает pHash+резкость/pixel-hash ИЗ ПАМЯТИ,
    /// освобождает байтовый бюджет. Ошибка декода одного файла — пропуск, цикл продолжается.</summary>
    private static async Task DecodeLoopAsync(ChannelReader<WorkItem> reader, IImageMatchService images,
        ImagePipelineOptions opts, ByteBudget budget, CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                opts.WaitGate?.Invoke(ct); // пауза действует и на декодерах
                // 3.7: резкость считается на той же ужатой копии, что pHash, — один декод на файл.
                var sig = images.ComputeSignatures(item.Content, ct);
                item.File.PHash = sig.PHash;
                item.File.Sharpness = sig.Sharpness;
                if (opts.ComputePixelHash) item.File.PixelHash = images.ComputePixelHash(item.Content, ct);
                opts.OnComputed?.Invoke(item.File, item.Path);
            }
            catch (OperationCanceledException) { throw; }
            catch { opts.OnSkipped?.Invoke(); }
            finally { budget.Release(item.Content.Length); }
        }
    }

    /// <summary>
    /// Бюджет байтов «в полёте» между чтением и декодом. Продюсер занимает размер файла ДО чтения
    /// и ждёт (backpressure), если декодеры не успевают освобождать; декодер освобождает после
    /// расчёта. Ожидание — Monitor с коротким тайм-аутом: отмена не оставляет продюсера висеть.
    /// </summary>
    private sealed class ByteBudget
    {
        private readonly object _lock = new();
        private readonly long _capacity;
        private long _used;

        public ByteBudget(long capacity) => _capacity = Math.Max(1, capacity);

        public void Acquire(long bytes, CancellationToken ct)
        {
            lock (_lock)
            {
                // «_used > 0»: одиночный элемент крупнее бюджета не дедлочит пустой канал
                // (страховка; такие файлы и так уходят старым путём с диска).
                while (_used > 0 && _used + bytes > _capacity)
                {
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_lock, 100);
                }
                ct.ThrowIfCancellationRequested();
                _used += bytes;
            }
        }

        public void Release(long bytes)
        {
            lock (_lock)
            {
                _used -= bytes;
                Monitor.PulseAll(_lock);
            }
        }
    }
}
