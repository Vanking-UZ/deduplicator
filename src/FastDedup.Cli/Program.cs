using System.Globalization;
using FastDedup.Engine;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cli = ParseArgs(args);
if (cli.ShowHelp)
{
    PrintHelp();
    return 0;
}
if (cli.Error is not null)
{
    Console.Error.WriteLine(cli.Error);
    Console.WriteLine();
    PrintHelp();
    return 2;
}

var roots = cli.Roots.Count > 0 ? cli.Roots : new List<string> { Directory.GetCurrentDirectory() };

Console.WriteLine("FastDedup — поиск дубликатов файлов");
Console.WriteLine("Сканирую: " + string.Join(", ", roots));

if (OperatingSystem.IsWindows() && !EngineEnvironment.IsElevated())
    Console.WriteLine("MFT/USN недоступен: используется совместимый режим без повышенных прав.");
Console.WriteLine();

var progress = new Progress<ScanProgress>(p =>
    Console.Write($"\r{PhaseName(p.Phase),-22} {p.Count,12:N0}          "));

var engine = new DedupOrchestrator();
var options = new ScanOptions
{
    Roots = roots,
    HeadTailChunkBytes = cli.HeadTailChunkBytes,
    MinFileSize = cli.MinFileSize,
    MaxFileSize = cli.MaxFileSize,
    IncludeReparsePoints = cli.IncludeReparsePoints,
    SkipHiddenSystem = cli.SkipHiddenSystem,
    ExcludedExtensions = cli.ExcludedExtensions,
    ExcludedDirectoryNames = cli.ExcludedDirectoryNames,
    ForceStorageType = cli.ForceStorageType,
    MaxStatParallelism = cli.MaxStatParallelism,
    MaxContentParallelism = cli.MaxContentParallelism,
    ChannelCapacity = cli.ChannelCapacity,
    MaxVolumeParallelism = cli.MaxVolumeParallelism,
    IncludeHardLinkAliases = cli.IncludeHardLinkAliases,
    VerifyStableSize = cli.VerifyStableSize
};

ScanResult result;
try
{
    result = await engine.ScanAsync(options, progress);
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.Error.WriteLine("Сканирование прервано: " + ex.Message);
    return 1;
}

Console.WriteLine();
Console.WriteLine();

Console.WriteLine("=== Носители ===");
foreach (var kv in result.VolumeStorage)
    Console.WriteLine($"  {kv.Key}   {kv.Value}");
Console.WriteLine();

var s = result.Stats;
Console.WriteLine("=== Статистика ===");
Console.WriteLine($"  Файлов найдено:             {s.FilesSeen,14:N0}");
Console.WriteLine($"  После фильтра размера:      {s.AfterSizeFilter,14:N0}");
Console.WriteLine($"  Прошли хэш head/tail:       {s.AfterHeadTail,14:N0}");
Console.WriteLine($"  Полный хэш:                 {s.FullHashed,14:N0}");
Console.WriteLine($"  Прочитано с диска:          {Bytes(s.BytesRead),14}");
Console.WriteLine($"  Время:                      {s.Elapsed.TotalSeconds,11:F1} c");
Console.WriteLine($"  Групп дубликатов:           {result.Groups.Count,14:N0}");
Console.WriteLine($"  Можно освободить:           {Bytes(s.ReclaimableBytes),14}");
if (s.HardLinkAliasesSkipped > 0)
    Console.WriteLine($"  Hardlink-алиасов схлопнуто: {s.HardLinkAliasesSkipped,14:N0}");
if (s.FilesChangedSkipped > 0)
    Console.WriteLine($"  Изменились во время скана:  {s.FilesChangedSkipped,14:N0}");
if (result.Errors.Count > 0)
    Console.WriteLine($"  Пропущено с ошибкой:        {result.Errors.Count,14:N0}");
Console.WriteLine();

if (result.Groups.Count > 0)
{
    Console.WriteLine("=== ТОП-20 групп (по освобождаемому объёму) ===");
    foreach (var g in result.Groups.OrderByDescending(g => g.ReclaimableBytes).Take(20))
    {
        Console.WriteLine($"  [{Bytes(g.Size)} × {g.Files.Count}]  освободит {Bytes(g.ReclaimableBytes)}");
        Console.WriteLine($"     оригинал: {g.SuggestedOriginal}");
        foreach (var f in g.Files.Where(f => !string.Equals(f, g.SuggestedOriginal, StringComparison.Ordinal)))
            Console.WriteLine($"     дубль:    {f}");
    }
    Console.WriteLine();
}

if (result.Errors.Count > 0)
{
    Console.WriteLine("=== Ошибки (первые 20) ===");
    foreach (var e in result.Errors.Take(20))
        Console.WriteLine($"  {e.Path} :: {e.Message}");
    Console.WriteLine();
}

return 0;

static CliArgs ParseArgs(string[] args)
{
    var cli = new CliArgs();
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        try
        {
            switch (a.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    cli.ShowHelp = true;
                    break;
                case "--head-tail-kb":
                    cli.HeadTailChunkBytes = checked((int)ParsePositiveLong(Next(args, ref i, a)) * 1024);
                    break;
                case "--min-size":
                    cli.MinFileSize = ParseSize(Next(args, ref i, a));
                    break;
                case "--max-size":
                    cli.MaxFileSize = ParseSize(Next(args, ref i, a));
                    break;
                case "--stat-dop":
                    cli.MaxStatParallelism = (int)ParsePositiveLong(Next(args, ref i, a));
                    break;
                case "--content-dop":
                    cli.MaxContentParallelism = (int)ParsePositiveLong(Next(args, ref i, a));
                    break;
                case "--volume-dop":
                    cli.MaxVolumeParallelism = (int)ParsePositiveLong(Next(args, ref i, a));
                    break;
                case "--channel":
                    cli.ChannelCapacity = (int)ParsePositiveLong(Next(args, ref i, a));
                    break;
                case "--storage":
                    cli.ForceStorageType = ParseStorage(Next(args, ref i, a));
                    break;
                case "--include-reparse":
                    cli.IncludeReparsePoints = true;
                    break;
                case "--include-hardlinks":
                    cli.IncludeHardLinkAliases = true;
                    break;
                case "--skip-hidden-system":
                    cli.SkipHiddenSystem = true;
                    break;
                case "--no-stable-size-check":
                    cli.VerifyStableSize = false;
                    break;
                case "--exclude-ext":
                    cli.ExcludedExtensions = SplitList(Next(args, ref i, a)).Select(NormalizeExt).ToArray();
                    break;
                case "--exclude-dir":
                    cli.ExcludedDirectoryNames = SplitList(Next(args, ref i, a)).ToArray();
                    break;
                default:
                    if (a.StartsWith('-')) cli.Error = "Неизвестный аргумент: " + a;
                    else cli.Roots.Add(a);
                    break;
            }
        }
        catch (Exception ex)
        {
            cli.Error = $"Ошибка в аргументе {a}: {ex.Message}";
        }

        if (cli.Error is not null) break;
    }
    return cli;
}

static string Next(string[] args, ref int i, string name)
{
    if (i + 1 >= args.Length) throw new ArgumentException($"для {name} нужно значение");
    return args[++i];
}

static long ParsePositiveLong(string raw)
{
    var value = ParseSize(raw);
    if (value <= 0) throw new ArgumentOutOfRangeException(nameof(raw), "значение должно быть > 0");
    return value;
}

static long ParseSize(string raw)
{
    var s = raw.Trim().ToLowerInvariant().Replace(" ", "");
    if (s.Length == 0) throw new FormatException("пустой размер");

    long mul = 1;
    foreach (var (suffix, factor) in new[]
             {
                 ("kib", 1024L), ("kb", 1024L), ("k", 1024L),
                 ("mib", 1024L * 1024), ("mb", 1024L * 1024), ("m", 1024L * 1024),
                 ("gib", 1024L * 1024 * 1024), ("gb", 1024L * 1024 * 1024), ("g", 1024L * 1024 * 1024),
                 ("tib", 1024L * 1024 * 1024 * 1024), ("tb", 1024L * 1024 * 1024 * 1024), ("t", 1024L * 1024 * 1024 * 1024),
                 ("b", 1L)
             })
    {
        if (!s.EndsWith(suffix, StringComparison.Ordinal)) continue;
        mul = factor;
        s = s[..^suffix.Length];
        break;
    }

    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        throw new FormatException("не удалось разобрать размер: " + raw);
    if (value < 0) throw new ArgumentOutOfRangeException(nameof(raw), "размер не может быть отрицательным");
    return checked((long)(value * mul));
}

static StorageType ParseStorage(string raw) => raw.Trim().ToLowerInvariant() switch
{
    "ssd" or "nvme" => StorageType.Ssd,
    "hdd" or "disk" => StorageType.Hdd,
    "unknown" or "auto" => StorageType.Unknown,
    _ => throw new FormatException("storage должен быть ssd, hdd или unknown")
};

static IEnumerable<string> SplitList(string raw) => raw
    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(s => s.Length > 0);

static string NormalizeExt(string ext)
{
    ext = ext.Trim();
    return ext.Length == 0 || ext[0] == '.' ? ext : "." + ext;
}

static string PhaseName(ScanPhase phase) => phase switch
{
    ScanPhase.Enumerating => "Сбор файлов",
    ScanPhase.SizeFiltered => "Фильтр по размеру",
    ScanPhase.HeadTailHashing => "Хэш head/tail",
    ScanPhase.FullHashing => "Полный хэш",
    ScanPhase.ByteCompare => "Сверка байтов",
    ScanPhase.Done => "Готово",
    _ => phase.ToString()
};

static string Bytes(long b)
{
    string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
    double v = b;
    int i = 0;
    while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
    return i == 0 ? $"{b} {u[i]}" : $"{v:F1} {u[i]}";
}

static void PrintHelp()
{
    Console.WriteLine("""
FastDedup — быстрый поиск дубликатов

Запуск:
  fastdedup [опции] [путь1] [путь2] ...

Опции производительности:
  --head-tail-kb N        размер head/tail чанка, КБ (по умолчанию 64)
  --stat-dop N            потоков metadata-stage
  --content-dop N         потоков чтения содержимого на том
  --volume-dop N          сколько томов хэшировать параллельно
  --channel N             ёмкость очереди между enumeration и metadata
  --storage ssd|hdd       принудительная политика носителя

Фильтры:
  --min-size 1mb          минимальный размер файла (по умолчанию 1 байт)
  --max-size 50gb         максимальный размер файла
  --exclude-ext .tmp,.log исключить расширения
  --exclude-dir .git,node_modules исключить поддеревья
  --skip-hidden-system    пропускать hidden/system
  --include-reparse       заходить в reparse points (осторожно: возможны циклы)

Безопасность:
  --include-hardlinks     показывать hardlink-алиасы как дубликаты
  --no-stable-size-check  не проверять изменение размера во время скана
""");
}

sealed class CliArgs
{
    public List<string> Roots { get; } = new();
    public bool ShowHelp { get; set; }
    public string? Error { get; set; }
    public int HeadTailChunkBytes { get; set; } = 64 * 1024;
    public long MinFileSize { get; set; } = 1;
    public long? MaxFileSize { get; set; }
    public bool IncludeReparsePoints { get; set; }
    public bool SkipHiddenSystem { get; set; }
    public IReadOnlyCollection<string> ExcludedExtensions { get; set; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ExcludedDirectoryNames { get; set; } = Array.Empty<string>();
    public StorageType? ForceStorageType { get; set; }
    public int? MaxStatParallelism { get; set; }
    public int? MaxContentParallelism { get; set; }
    public int? MaxVolumeParallelism { get; set; }
    public int? ChannelCapacity { get; set; }
    public bool IncludeHardLinkAliases { get; set; }
    public bool VerifyStableSize { get; set; } = true;
}
