using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>Перечисление файлов с метаданными напрямую (без второго stat на каждый файл).</summary>
public interface IMetadataEnumerator
{
    IEnumerable<EnumeratedFile> EnumerateMetadata(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct);
}

/// <summary>
/// Универсальный быстрый перечислитель Win32: <c>FindFirstFileExW</c> отдаёт размер, время и атрибуты
/// прямо в <c>WIN32_FIND_DATA</c>, поэтому НЕ нужен отдельный <c>GetFileAttributesEx</c> на каждый файл
/// (в этом было основное замедление старого fallback на exFAT/USB/сети). Ручной стек каталогов (без
/// рекурсии и переполнения), без сортировки, без аллокаций FileInfo/DirectoryInfo. Работает на
/// NTFS/exFAT/FAT32/ReFS и сетевых путях. Длинные пути — через префикс \\?\; UNC — \\?\UNC\.
/// При DOP &gt; 1 очередь каталогов разбирает пул воркеров: на носителях с высокой латентностью
/// каждого вызова (USB-мост, тем более сеть/UNC) обход перестаёт упираться в латентность одиночных
/// FindFirstFileEx. Порядок выдачи файлов при этом недетерминированный (набор — тот же).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32FindMetadataEnumerator : IMetadataEnumerator
{
    private const int FindExInfoBasic = 1;          // не запрашивать cAlternateFileName — быстрее
    private const int FindExSearchNameMatch = 0;
    private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;
    private const int ERROR_NO_MORE_FILES = 18;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    public IEnumerable<EnumeratedFile> EnumerateMetadata(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
        => EnumerateMetadata(root, options, errors, ct, degreeOfParallelism: 1);

    /// <summary>
    /// DOP == 1 — СТАРЫЙ однопоточный обход (детерминированный порядок, нулевой риск регрессии на
    /// носителях со штрафом позиционирования). DOP &gt; 1 — параллельный обход очереди каталогов;
    /// набор файлов и семантика фильтров те же, порядок — нет.
    /// </summary>
    public IEnumerable<EnumeratedFile> EnumerateMetadata(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct,
        int degreeOfParallelism)
        => degreeOfParallelism <= 1
            ? EnumerateSequential(root, options, errors, ct)
            : EnumerateParallel(root, options, errors, ct, degreeOfParallelism);

    private static IEnumerable<EnumeratedFile> EnumerateSequential(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
    {
        var stack = new Stack<string>();
        // identity (том+индекс) уже посещённых reparse-каталогов — защита от циклов джанкшенов/симлинков.
        var visitedReparse = new HashSet<(uint, ulong)>();

        if (!TryGetRootFullPath(root, errors, out var rootFull)) yield break;
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var ef in EnumerateOneDirectory(dir, options, errors, stack.Push,
                         id => visitedReparse.Add(id), ct))
                yield return ef;
        }
    }

    /// <summary>
    /// Параллельный обход: пул воркеров разбирает очередь каталогов, каждый перечисляет свой каталог
    /// тем же <see cref="EnumerateOneDirectory"/>, найденные подкаталоги кладёт обратно в очередь,
    /// файлы — в bounded-выход (паттерн <c>FastFileLister.StatPaths</c>). Завершение: счётчик
    /// «каталоги в работе + в очереди» (Interlocked); ноль — CompleteAdding очереди. Отмена ct рвёт
    /// и очередь, и выход — воркеры завершаются без зависаний и утечек.
    /// </summary>
    private static IEnumerable<EnumeratedFile> EnumerateParallel(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct, int dop)
    {
        if (!TryGetRootFullPath(root, errors, out var rootFull)) yield break;

        // identity уже посещённых reparse-каталогов; общий для всех воркеров — потокобезопасный.
        var visitedReparse = new ConcurrentDictionary<(uint, ulong), byte>();
        var dirs = new BlockingCollection<string>(new ConcurrentQueue<string>());
        var output = new BlockingCollection<EnumeratedFile>(boundedCapacity: 50_000);
        int pending = 1; // каталоги «в очереди + в работе»; 1 — корень
        dirs.Add(rootFull);

        void Worker()
        {
            try
            {
                foreach (var dir in dirs.GetConsumingEnumerable(ct))
                {
                    try
                    {
                        foreach (var ef in EnumerateOneDirectory(dir, options, errors,
                                     sub => { Interlocked.Increment(ref pending); dirs.Add(sub); },
                                     id => visitedReparse.TryAdd(id, 0), ct))
                            output.Add(ef, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Сбой одного каталога не должен убивать воркер: иначе pending не дойдёт до
                        // нуля и остальные воркеры зависнут на очереди. Семантика прежняя — в мешок.
                        errors.Add(new ScanError(dir, "Обход каталога: " + ex.Message));
                    }
                    finally
                    {
                        // Каталог обработан (или отменён). Ноль «в работе + в очереди» — обход закончен.
                        if (Interlocked.Decrement(ref pending) == 0) dirs.CompleteAdding();
                    }
                }
            }
            catch (OperationCanceledException) { /* отмена — штатный выход воркера */ }
        }

        var pump = Task.Run(() =>
        {
            var workers = new Task[dop];
            for (int i = 0; i < dop; i++) workers[i] = Task.Run(Worker, CancellationToken.None);
            try { Task.WaitAll(workers); }
            finally { output.CompleteAdding(); }
        }, CancellationToken.None);

        foreach (var item in output.GetConsumingEnumerable(ct))
            yield return item;

        pump.GetAwaiter().GetResult(); // пробросить неожиданные исключения воркеров
    }

    /// <summary>
    /// Некорректный/слишком длинный корень не должен ронять весь скан (другие корни/тома) —
    /// фиксируем как ошибку этого корня и выходим пусто.
    /// </summary>
    private static bool TryGetRootFullPath(string root, ConcurrentBag<ScanError> errors, out string rootFull)
    {
        try { rootFull = Path.GetFullPath(root); return true; }
        catch (Exception ex)
        {
            errors.Add(new ScanError(root, "Некорректный корень: " + ex.Message));
            rootFull = "";
            return false;
        }
    }

    /// <summary>
    /// Перечислить ОДИН каталог: файлы отдаются лениво, найденные подкаталоги — в pushDirectory.
    /// markReparseVisited возвращает true при ПЕРВОМ визите identity (том+индекс) reparse-каталога:
    /// повторный визит — цикл джанкшена, внутрь не заходим. Вынесено из старого однопоточного обхода
    /// БЕЗ изменения семантики фильтров, чтобы параллельные воркеры исполняли ровно тот же код.
    /// </summary>
    private static IEnumerable<EnumeratedFile> EnumerateOneDirectory(
        string dir, ScanOptions options, ConcurrentBag<ScanError> errors,
        Action<string> pushDirectory, Func<(uint, ulong), bool> markReparseVisited, CancellationToken ct)
    {
        // Префикс каталога с разделителем на конце: и для поиска (\\?\dir\*), и для сборки путей.
        string prefix = dir.EndsWith('\\') || dir.EndsWith('/') ? dir : dir + "\\";

        IntPtr h = FindFirstFileExW(Native.LongPath(prefix) + "*", FindExInfoBasic,
            out var data, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
        if (h == INVALID_HANDLE_VALUE)
        {
            // Недоступная папка — считаем ошибку, но скан целиком не валим.
            errors.Add(new ScanError(dir, "FindFirstFileEx error " + Marshal.GetLastWin32Error()));
            yield break;
        }

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                string name = data.cFileName;
                if (name is "." or "..") continue;

                uint attrs = data.dwFileAttributes;
                string full = prefix + name;

                if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    bool isReparse = (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                    if (isReparse && !options.IncludeReparsePoints)
                    {
                        options.OnReparseSkipped?.Invoke(full, true);
                        continue;
                    }
                    if (options.SkipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) continue;
                    if (ScanFilters.ShouldSkipDirectoryName(name, options)) continue;
                    // В reparse-каталог заходим лишь ОДИН раз по его identity (том+индекс): джанкшен на
                    // предка иначе зациклил бы обход навсегда. Не опознали identity — не рискуем, пропускаем.
                    if (isReparse)
                    {
                        if (!Native.TryGetFileIdentity(full, out var rid)) continue;
                        if (!markReparseVisited((rid.VolumeSerialNumber, rid.FileIndex))) continue;
                    }
                    pushDirectory(full);
                }
                else
                {
                    long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    if (ScanFilters.ShouldSkipMetadata(full, size, attrs, options))
                    {
                        // Reparse-ФАЙЛ (облачный плейсхолдер/симлинк) — считаем, а не молчим.
                        if ((attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !options.IncludeReparsePoints)
                            options.OnReparseSkipped?.Invoke(full, false);
                        continue;
                    }
                    long cr = ((long)data.ftCreationTime.dwHighDateTime << 32) | data.ftCreationTime.dwLowDateTime;
                    long lw = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime;
                    yield return new EnumeratedFile(full, size, cr, lw, attrs);
                }
            }
            while (FindNextFileW(h, out data));

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NO_MORE_FILES && err != 0)
                errors.Add(new ScanError(dir, "FindNextFile error " + err));
        }
        finally
        {
            FindClose(h);
        }
    }
}
