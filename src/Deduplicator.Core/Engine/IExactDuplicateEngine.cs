using Deduplicator.Core.Models;

namespace Deduplicator.Core.Engine;

/// <summary>Контекст одного прогона exact-воронки (метаданные уже в БД, этап перечисления выполнен).</summary>
public sealed class ExactScanRequest
{
    /// <summary>Точки монтирования подключённых томов (volumeId → корень).</summary>
    public required IReadOnlyDictionary<long, string> MountById { get; init; }

    /// <summary>Каталог томов (для побайтовой сверки/группировки, включая отключённые).</summary>
    public required IReadOnlyDictionary<long, VolumeRecord> VolumesById { get; init; }

    /// <summary>Входит ли файл в область сканирования (том целиком или конкретные папки).</summary>
    public required Func<FileRecord, bool> InScope { get; init; }

    /// <summary>План чтения на каждый том (HDD послед., SSD параллельно).</summary>
    public required IReadOnlyDictionary<long, VolumeReadPlan> ReadPlans { get; init; }

    /// <summary>Сколько томов читать одновременно.</summary>
    public required int MaxVolumeParallelism { get; init; }

    /// <summary>Запрошенный пользователем параллелизм (fallback, если плана тома нет).</summary>
    public required int RequestedParallelism { get; init; }

    /// <summary>Правило выбора эталона группы.</summary>
    public required RepresentativeRule Rule { get; init; }

    /// <summary>
    /// «Файл в папке-эталоне» для правила Smart (2.10). Строится фасадом (ScanService) из
    /// AppSettings.ReferenceFolders + точек монтирования — Core настроек не знает.
    /// null — бонус папки-эталона не начисляется, остальные эвристики Smart работают.
    /// </summary>
    public Func<FileRecord, bool>? IsPreferredKeep { get; init; }

    /// <summary>Id сессии скана — точные группы помечаются им (scope-aware результаты, P0.1).</summary>
    public long SessionId { get; init; }
}

/// <summary>Сводная статистика exact-воронки.</summary>
public sealed class ExactScanResult
{
    public long SizeCandidates { get; set; }
    public long PartialHashed { get; set; }
    public long FullHashed { get; set; }
    public long GroupsCreated { get; set; }
}

/// <summary>
/// Канал прогресса/управления из движка наружу (в <c>ScanService</c>): троттлинг, сессии и
/// пауза/отмена остаются на стороне фасада, движок их только дёргает.
/// </summary>
public interface IExactScanReporter
{
    /// <summary>Барьер паузы; бросает при отмене.</summary>
    void WaitGate();
    /// <summary>Сменить крупный этап (немедленно).</summary>
    void Stage(ScanStage stage);
    /// <summary>Сменить этап, только если он реально поменялся (без мигания).</summary>
    void StageQuiet(ScanStage stage);
    void FileHashed(string path, long bytes);
    void Skipped();
    void ChangedSkipped();
    void Fraction(double value);
    void ExactGroups(long count);
    /// <summary>Контрольная точка для сохранения прогресса сессии (возобновляемость).</summary>
    void SessionTick();

    /// <summary>
    /// Принудительно проверить identity всех активных носителей перед пакетной записью. Default
    /// оставляет совместимость тестовых/внешних репортёров, которым сторож не нужен.
    /// </summary>
    void VerifyVolumes() { }
}

/// <summary>
/// Движок точных дубликатов v2. Заменяет старую SQLite-воронку с N+1 по размеру: потоковая выборка
/// кандидатов одним запросом → частичный хэш → полный хэш (только для коллизий) → точные группы с
/// побайтовой сверкой. Хэши сохраняются по ходу; per-volume DOP и проверка стабильности размера.
/// </summary>
public interface IExactDuplicateEngine
{
    ExactScanResult ScanExact(ExactScanRequest request, IExactScanReporter reporter, CancellationToken ct);
}
