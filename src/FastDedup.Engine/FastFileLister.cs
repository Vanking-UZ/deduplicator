using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>Лёгкая запись о файле для внешнего потребителя. Без хэшей.</summary>
public readonly record struct EnumeratedFile(
    string FullPath, long Size, long CreationRaw, long LastWriteRaw, uint Attributes);

public sealed class FastListerOptions
{
    /// <summary>Заходить ли внутрь reparse-точек (симлинки/джанкшены). По умолчанию нет — защита от циклов.</summary>
    public bool IncludeReparsePoints { get; init; } = false;

    /// <summary>Параллелизм стадии stat (только MFT-путь). null — авто.</summary>
    public int? StatParallelism { get; init; }

    /// <summary>
    /// Опциональный клиент отдельного подписанного MFT-helper. Он используется только для целого
    /// локального NTFS-тома без повышения основного процесса и только если корень заранее разрешён.
    /// </summary>
    public IElevatedMftBroker? ElevatedMftBroker { get; init; }

    /// <summary>Пропускать скрытые/системные файлы И каталоги.</summary>
    public bool SkipHiddenSystem { get; init; } = false;

    /// <summary>Какой бэкенд выбран для этого корня (для UI/диагностики).</summary>
    public Action<BackendDecision>? OnBackend { get; init; }

    /// <summary>Ошибка перечисления (недоступная папка, сбой API).</summary>
    public Action<ScanError>? OnError { get; init; }

    /// <summary>Пропущен reparse/облачный элемент (путь, каталог ли) — для счётчика в UI.</summary>
    public Action<string, bool>? OnReparseSkipped { get; init; }
}

/// <summary>
/// Быстрый перечислитель файлов. Выбирает бэкенд по матрице (<see cref="BackendSelector"/>):
///   • целый том NTFS под админом → турбо MFT/USN (плюс stat — USN-записи не дают размер/время);
///   • всё остальное (exFAT/FAT32/ReFS/сеть/папка/без админа) → совместимый быстрый режим Win32Find,
///     где метаданные приходят прямо из FindFirstFileExW БЕЗ второго stat на каждый файл.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FastFileLister
{
    public IEnumerable<EnumeratedFile> Enumerate(string root, FastListerOptions options, CancellationToken ct)
    {
        var errors = new ConcurrentBag<ScanError>();
        var decision = DecideBackend(root);

        var engineOpts = new ScanOptions
        {
            Roots = new[] { root },
            IncludeReparsePoints = options.IncludeReparsePoints,
            SkipHiddenSystem = options.SkipHiddenSystem,
            OnReparseSkipped = options.OnReparseSkipped
        };

        try
        {
            if (decision.Backend == EnumerationBackend.NtfsMft)
            {
                // MFT может оказаться недоступен (неподдерживаемые записи / нет прав) — тогда
                // переключаемся на Win32Find и сообщаем реальный бэкенд через OnBackend изнутри.
                foreach (var ef in EnumerateViaMftOrFallback(root, engineOpts, options, errors, ct, decision))
                    yield return ef;
            }
            else if (decision.Reason == BackendFallbackReason.NotElevated &&
                     options.ElevatedMftBroker?.CanEnumerate(root) == true)
            {
                foreach (var ef in EnumerateViaElevatedMftOrFallback(
                             root, engineOpts, options, errors, ct, decision, options.ElevatedMftBroker))
                    yield return ef;
            }
            else
            {
                options.OnBackend?.Invoke(decision);
                // Совместимый быстрый режим: метаданные напрямую из FindFirstFileExW (без второго stat).
                // Очередь каталогов разбирается параллельно (см. Win32FindDop); DOP 1 — старый путь.
                foreach (var ef in new Win32FindMetadataEnumerator()
                             .EnumerateMetadata(root, engineOpts, errors, ct, Win32FindDop(decision, options)))
                    yield return ef;
            }
        }
        finally
        {
            // Ошибки (недоступные папки, сбой MFT) отдаём наружу в любом случае, в т.ч. при отмене.
            if (options.OnError != null)
                foreach (var e in errors) options.OnError(e);
        }
    }

    /// <summary>
    /// Отдельный helper передаёт сырые MFT-записи; клиент не отдаёт ни одного пути до Complete.
    /// Любой отказ UAC, trust-gate, EOF, timeout или несовпадение count приводит к ПОЛНОМУ
    /// Win32Find-обходу того же корня. QueryJournal/ReadDelta сюда не относятся и не повышаются.
    /// </summary>
    private static IEnumerable<EnumeratedFile> EnumerateViaElevatedMftOrFallback(
        string root, ScanOptions engineOpts, FastListerOptions options,
        ConcurrentBag<ScanError> errors, CancellationToken ct, BackendDecision originalDecision,
        IElevatedMftBroker broker)
    {
        MftEnumerationUnavailableException? memoryFailure = PlanMftMemory(root);
        if (memoryFailure is not null)
        {
            var lowMemory = originalDecision with
            {
                Backend = EnumerationBackend.Win32Find,
                Reason = memoryFailure.Reason,
                TechnicalDetails = memoryFailure.Message
            };
            options.OnBackend?.Invoke(lowMemory);
            foreach (var ef in new Win32FindMetadataEnumerator().EnumerateMetadata(
                         root, engineOpts, errors, ct, Win32FindDop(lowMemory, options)))
                yield return ef;
            yield break;
        }

        IEnumerable<EnumeratedFile> source = StatPaths(
            broker.Enumerate(root, options.IncludeReparsePoints, options.SkipHiddenSystem,
                options.OnReparseSkipped, ct), options, ct);
        using IEnumerator<EnumeratedFile> iterator = source.GetEnumerator();
        ElevatedMftBrokerUnavailableException? helperFailure = null;
        while (true)
        {
            bool moved;
            try { moved = iterator.MoveNext(); }
            catch (Exception ex) when (TryFindHelperFailure(ex, out helperFailure))
            {
                break;
            }

            if (!moved)
            {
                options.OnBackend?.Invoke(originalDecision with
                {
                    Backend = EnumerationBackend.NtfsMftHelper,
                    Reason = BackendFallbackReason.None,
                    TechnicalDetails = "Полный MFT-обход выполнен отдельным подписанным helper-процессом."
                });
                yield break;
            }
            yield return iterator.Current;
        }

        var fallback = originalDecision with
        {
            Backend = EnumerationBackend.Win32Find,
            Reason = helperFailure!.Reason,
            TechnicalDetails = helperFailure.Message
        };
        options.OnBackend?.Invoke(fallback);
        foreach (var ef in new Win32FindMetadataEnumerator().EnumerateMetadata(
                     root, engineOpts, errors, ct, Win32FindDop(fallback, options)))
            yield return ef;
    }

    private static bool TryFindHelperFailure(
        Exception error, out ElevatedMftBrokerUnavailableException? helperFailure)
    {
        if (error is ElevatedMftBrokerUnavailableException direct)
        {
            helperFailure = direct;
            return true;
        }
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                if (TryFindHelperFailure(inner, out helperFailure)) return true;
        }
        else if (error.InnerException is not null &&
                 TryFindHelperFailure(error.InnerException, out helperFailure))
            return true;

        helperFailure = null;
        return false;
    }

    /// <summary>
    /// Пытаемся прочитать MFT; если он недоступен — НЕ старый Directory+stat, а совместимый быстрый
    /// Win32Find (метаданные напрямую). Решение о бэкенде (NtfsMft подтверждён / Win32Find откат)
    /// сообщаем через OnBackend ровно один раз, после того как стало известно, что реально используем.
    /// </summary>
    private static IEnumerable<EnumeratedFile> EnumerateViaMftOrFallback(
        string root, ScanOptions engineOpts, FastListerOptions options,
        ConcurrentBag<ScanError> errors, CancellationToken ct, BackendDecision decision)
    {
        Dictionary<ulong, UsnEnumerator.Rec>? map = null;
        string drive = "";
        // RAM-aware (2.11): прогноз памяти карты MFT ДО её чтения. Том на 5–10 млн записей на машине
        // с 2–4 ГБ RAM уводит Dictionary в своп — честнее сразу совместимый режим, чем «турбо»-тормоза.
        MftEnumerationUnavailableException? failure = PlanMftMemory(root);
        if (failure is null)
        {
            try { (map, drive) = UsnEnumerator.ReadMap(root, ct); }
            catch (MftEnumerationUnavailableException ex) { failure = ex; }
        }

        IEnumerable<EnumeratedFile> source;
        if (map is null)
        {
            var fb = decision with
            {
                Backend = EnumerationBackend.Win32Find,
                Reason = failure!.Reason,
                TechnicalDetails = failure.Message
            };
            options.OnBackend?.Invoke(fb);
            // Это не ошибка полноты: Win32Find ниже заново и полностью обходит тот же корень.
            // Причина fallback уже передана через OnBackend/TechnicalDetails; добавление в OnError
            // ошибочно помечало успешный совместимый обход как неполный и запрещало checkpoint.
            source = new Win32FindMetadataEnumerator().EnumerateMetadata(root, engineOpts, errors, ct,
                Win32FindDop(fb, options));
        }
        else
        {
            options.OnBackend?.Invoke(decision); // подтверждённый турбо NtfsMft
            source = StatPaths(UsnEnumerator.BuildPaths(map, drive, root, engineOpts, ct), options, ct);
        }

        // Единственная точка yield (без try/catch вокруг неё — требование итераторов C#).
        foreach (var ef in source) yield return ef;
    }

    /// <summary>
    /// DOP обхода каталогов для Win32Find-бэкенда. Метаданные каталогов — не содержимое: даже на HDD
    /// умеренный параллелизм допустим, и степень задаёт план тома (StatParallelism = MetadataDop из
    /// ScanService: HDD/съёмные ≤ 2, SSD выше). Сеть/UNC — фиксированно 8: там обход упирается в
    /// латентность каждого FindFirstFileEx, а не в позиционирование головки. DOP 1 включает СТАРЫЙ
    /// однопоточный путь внутри перечислителя (нулевой риск регрессии).
    /// </summary>
    private static int Win32FindDop(BackendDecision decision, FastListerOptions options)
        => decision.Backend == EnumerationBackend.Win32FindNetwork
            ? 8
            : Math.Max(1, options.StatParallelism ?? Math.Min(Environment.ProcessorCount, 8));

    /// <summary>
    /// Оценка «влезет ли карта MFT в память» ДО её чтения. FSCTL-запрос guarded: любая ошибка →
    /// null → решаем как раньше (пробуем MFT). null — читать можно; иначе готовое исключение-причина
    /// LowMemory для общего пути фолбэка (тот же, что при MftEnumerationUnavailableException).
    /// </summary>
    private static MftEnumerationUnavailableException? PlanMftMemory(string root)
    {
        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(root));
            if (rootPath is not { Length: >= 2 } || rootPath[1] != ':') return null;

            long? mftLen = Native.TryGetMftValidDataLength(rootPath[..2]);
            if (mftLen is not long len) return null; // размер MFT не узнали — поведение прежнее

            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (MftMemoryPlanner.ShouldUseMft(len, available)) return null;

            return new MftEnumerationUnavailableException(BackendFallbackReason.LowMemory,
                $"прогноз карты MFT ≈ {MftMemoryPlanner.EstimateMapBytes(len) / (1024 * 1024)} МБ " +
                $"при доступных {available / (1024 * 1024)} МБ");
        }
        catch { return null; } // оценка не должна ломать сам скан
    }

    /// <summary>MFT-путь: USN отдаёт пути, размер/время берём stat-ом (USN-записи их не содержат).</summary>
    private static IEnumerable<EnumeratedFile> StatPaths(
        IEnumerable<string> paths, FastListerOptions options, CancellationToken ct)
    {
        int dop = options.StatParallelism ?? Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        var output = new BlockingCollection<EnumeratedFile>(boundedCapacity: 50_000);

        var pump = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(paths,
                    new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                    path =>
                    {
                        if (!Native.TryGetFileInfo(path, out long size, out uint attrs, out long cr, out long lw)) return;
                        if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) return;
                        if (!options.IncludeReparsePoints && (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        {
                            options.OnReparseSkipped?.Invoke(path, false);
                            return;
                        }
                        if (options.SkipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) return;
                        output.Add(new EnumeratedFile(path, size, cr, lw, attrs), ct);
                    });
            }
            catch (OperationCanceledException) { }
            finally { output.CompleteAdding(); }
        }, ct);

        foreach (var item in output.GetConsumingEnumerable(ct))
            yield return item;

        pump.GetAwaiter().GetResult(); // пробросить исключения насоса
    }

    /// <summary>Выбор бэкенда: определяем ФС/тип носителя/целый ли том и зовём чистый BackendSelector.</summary>
    private static BackendDecision DecideBackend(string root)
    {
        try
        {
            bool win = OperatingSystem.IsWindows();
            bool whole = BackendSelector.IsWholeVolumeRoot(root);
            string? fs = null;
            string? volRoot = null;
            System.IO.DriveType? driveType = null;

            if (BackendSelector.IsUncPath(root))
            {
                volRoot = root; // сеть/UNC — детали ФС недоступны, BackendSelector выберет сетевой режим
            }
            else if (win)
            {
                var rootPath = Path.GetPathRoot(Path.GetFullPath(root));
                if (rootPath is { Length: >= 2 } && rootPath[1] == ':')
                {
                    volRoot = rootPath[..2] + "\\";
                    try
                    {
                        var di = new DriveInfo(rootPath[..2] + "\\");
                        driveType = di.DriveType;
                        if (di.IsReady) fs = di.DriveFormat; // "NTFS"/"exFAT"/"FAT32"/"ReFS"…
                    }
                    catch { /* ФС/тип неизвестны → совместимый режим */ }
                }
            }

            return BackendSelector.Decide(root, volRoot, fs, driveType, win, Native.IsElevated(), whole);
        }
        catch
        {
            return new BackendDecision(root, null, null, null, false, false,
                EnumerationBackend.Win32Find, BackendFallbackReason.ApiError, null);
        }
    }
}
