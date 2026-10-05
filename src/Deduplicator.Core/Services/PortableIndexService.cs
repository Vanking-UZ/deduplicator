using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

/// <summary>
/// 3.4: переносимый кэш проверки на носителе — скрытый файл <c>.dedupindex</c> в корне съёмного
/// тома. Флешка, проверенная на одном компьютере, на другом (с этим приложением) сканируется почти
/// мгновенно: хэши берутся из кэша и не пересчитываются. Это чистый КЭШ: любое расхождение
/// (серийник, версии алгоритмов, метаданные файла, битый файл) молча игнорируется — безопасность
/// удаления прикрывают побайтовая сверка и перепроверка перед удалением, которые кэш не обходит.
/// </summary>
public interface IPortableIndexService
{
    /// <summary>Выгрузить кэш тома в корень носителя. Полностью guarded: любой сбой (readonly-носитель,
    /// нет прав, нет места) — лог + false, исключений наружу нет.</summary>
    bool TryExport(long volumeId, string mountRoot);

    /// <summary>Прочитать и проверить кэш с носителя. null — файла нет или он не подходит
    /// (чужой серийник, другие версии алгоритмов, битый/обрезанный файл); причина в логе.</summary>
    PortableIndexCache? TryImport(long volumeId, string mountRoot);
}

/// <summary>Заголовок файла .dedupindex: идентичность носителя и версии алгоритмов хэширования.</summary>
public sealed class PortableIndexHeader
{
    public int FormatVersion { get; set; }
    public string? VolumeSerial { get; set; }
    public int PartialVer { get; set; }
    public int PHashVer { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>Одна запись кэша: метаданные для строгой сверки + сохранённые хэши/признаки.
/// byte[] сериализуется System.Text.Json как base64 — компактно и без ручной кодировки.</summary>
public sealed class PortableIndexEntry
{
    public string RelPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public long MtimeTicksUtc { get; set; }
    public long CreatedTicksUtc { get; set; }
    public byte[]? PartialHash { get; set; }
    public byte[]? FullHash { get; set; }
    public byte[]? PHash { get; set; }
    public byte[]? PixelHash { get; set; }
    public byte[]? AudioFp { get; set; }
    public double? Sharpness { get; set; }
    public byte[]? VerifiedFullHash { get; set; }
}

/// <summary>Корневой документ файла .dedupindex (JSON внутри GZip).</summary>
public sealed class PortableIndexDocument
{
    public PortableIndexHeader? Header { get; set; }
    public List<PortableIndexEntry>? Entries { get; set; }
}

/// <summary>Проверенный кэш носителя, готовый к сверке при перечислении (lookup по RelPath).</summary>
public sealed class PortableIndexCache
{
    public PortableIndexCache(PortableIndexHeader header, Dictionary<string, PortableIndexEntry> byRelPath)
    {
        Header = header;
        ByRelPath = byRelPath;
    }

    public PortableIndexHeader Header { get; }
    public IReadOnlyDictionary<string, PortableIndexEntry> ByRelPath { get; }
}

public sealed class PortableIndexService : IPortableIndexService
{
    /// <summary>Имя файла кэша в корне носителя (скрытый+системный после записи, best-effort).</summary>
    public const string FileName = ".dedupindex";
    public const int FormatVersion = 1;

    /// <summary>Кап записей: у гигантских томов кэш не пишем и не читаем (защита от многосот-МБ
    /// файла на флешке и от подложного файла, раздувающего память при импорте).</summary>
    public const int MaxEntries = 500_000;
    public const long MaxCompressedBytes = 96L * 1024 * 1024;
    public const long MaxUncompressedBytes = 192L * 1024 * 1024;

    // Страница потоковой выгрузки (keyset по id): весь том в память не материализуется.
    private const int ExportPage = 2000;

    private static readonly JsonSerializerOptions ImportJsonOptions = CreateImportJsonOptions();

    private readonly VolumeRepository _volumes;
    private readonly FileRepository _files;

    public PortableIndexService(VolumeRepository volumes, FileRepository files)
    {
        _volumes = volumes;
        _files = files;
    }

    public bool TryExport(long volumeId, string mountRoot)
    {
        string? tmp = null;
        try
        {
            if (!Directory.Exists(mountRoot))
            {
                AppLog.Write($"Переносимый кэш: корень {mountRoot} недоступен — экспорт пропущен.");
                return false;
            }
            var serial = _volumes.GetById(volumeId)?.Serial;
            if (string.IsNullOrWhiteSpace(serial))
            {
                AppLog.Write($"Переносимый кэш: у тома #{volumeId} нет серийного номера — экспорт пропущен.");
                return false;
            }

            long count = _files.CountHashedByVolume(volumeId);
            if (count == 0)
            {
                AppLog.Write($"Переносимый кэш: на томе #{volumeId} нет строк с хэшами — экспорт пропущен.");
                return false;
            }
            if (count > MaxEntries)
            {
                AppLog.Write($"Переносимый кэш: строк с хэшами {count:N0} > {MaxEntries:N0} — экспорт пропущен (кап).");
                return false;
            }

            var header = new PortableIndexHeader
            {
                FormatVersion = FormatVersion,
                VolumeSerial = serial,
                PartialVer = HashService.CurrentPartialVersion,
                PHashVer = ImageMatchService.CurrentPHashVersion,
                CreatedAt = DateTimeOffset.Now,
                AppVersion = EngineBuildInfo.CoreVersion
            };

            var target = Path.Combine(mountRoot, FileName);
            tmp = target + SafeIo.TempSuffix;
            long written = 0;
            // Записи пишутся потоково через Utf8JsonWriter (страницы из БД → сразу в gzip),
            // публикация — атомарным переименованием (паттерн SafeIo): на носителе либо старый
            // валидный кэш, либо новый, но не «половина».
            using (var fs = new FileStream(LongPath.Fix(tmp), FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var writer = new Utf8JsonWriter(gz))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Header");
                JsonSerializer.Serialize(writer, header);
                writer.WritePropertyName("Entries");
                writer.WriteStartArray();
                long afterId = 0;
                while (true)
                {
                    var page = _files.GetHashedByVolumePage(volumeId, afterId, ExportPage);
                    if (page.Count == 0) break;
                    afterId = page[^1].Id;
                    foreach (var f in page)
                    {
                        JsonSerializer.Serialize(writer, ToEntry(f));
                        written++;
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            // Прежний кэш мог быть скрытым/системным — со снятыми атрибутами замена проходит.
            if (File.Exists(target))
            {
                try { File.SetAttributes(LongPath.Fix(target), FileAttributes.Normal); }
                catch { /* не смогли — Move ниже честно упадёт в общий catch */ }
            }
            File.Move(LongPath.Fix(tmp), LongPath.Fix(target), overwrite: true);
            tmp = null;

            // Скрытый+системный — чтобы файл не мозолил глаза в проводнике. Best-effort:
            // на FAT/сетевых носителях атрибуты могут не примениться — кэш работает и без них.
            try { File.SetAttributes(LongPath.Fix(target), File.GetAttributes(LongPath.Fix(target)) | FileAttributes.Hidden | FileAttributes.System); }
            catch { }

            AppLog.Write($"Переносимый кэш: экспортировано записей: {written:N0} → {target}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("PortableIndex.Export", ex);
            return false;
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(LongPath.Fix(tmp)); } catch { }
            }
        }
    }

    public PortableIndexCache? TryImport(long volumeId, string mountRoot)
    {
        try
        {
            var path = Path.Combine(mountRoot, FileName);
            if (!File.Exists(LongPath.Fix(path))) return null; // нет файла — обычный случай, без лога
            var compressedLength = new FileInfo(LongPath.Fix(path)).Length;
            if (compressedLength > MaxCompressedBytes)
            {
                AppLog.Write($"Переносимый кэш {path}: сжатый файл {compressedLength:N0} байт больше лимита " +
                             $"{MaxCompressedBytes:N0} — импорт отклонён.");
                return null;
            }

            var serial = _volumes.GetById(volumeId)?.Serial;
            if (string.IsNullOrWhiteSpace(serial))
            {
                AppLog.Write($"Переносимый кэш {path}: у тома #{volumeId} нет серийного номера — импорт пропущен.");
                return null;
            }

            PortableIndexDocument? doc;
            using (var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var bounded = new BoundedReadStream(gz, MaxUncompressedBytes))
                doc = JsonSerializer.Deserialize<PortableIndexDocument>(bounded, ImportJsonOptions);

            var header = doc?.Header;
            if (header is null || doc!.Entries is null)
            {
                AppLog.Write($"Переносимый кэш {path}: нет заголовка или записей — импорт отклонён.");
                return null;
            }
            if (header.FormatVersion != FormatVersion)
            {
                AppLog.Write($"Переносимый кэш {path}: формат v{header.FormatVersion} ≠ v{FormatVersion} — импорт отклонён.");
                return null;
            }
            // Идентичность носителя: кэш применим ТОЛЬКО к тому, на котором был создан.
            if (!string.Equals(header.VolumeSerial?.Trim(), serial.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write($"Переносимый кэш {path}: серийник «{header.VolumeSerial}» ≠ «{serial}» — импорт отклонён.");
                return null;
            }
            // Версии алгоритмов должны совпадать с ТЕКУЩИМИ: хэш другой версии в воронке бесполезен
            // и опасен смешением (тот же принцип, что partial_ver/phash_ver в БД).
            if (header.PartialVer != HashService.CurrentPartialVersion ||
                header.PHashVer != ImageMatchService.CurrentPHashVersion)
            {
                AppLog.Write($"Переносимый кэш {path}: версии алгоритмов partial={header.PartialVer}/pHash={header.PHashVer} " +
                             $"≠ текущих {HashService.CurrentPartialVersion}/{ImageMatchService.CurrentPHashVersion} — импорт отклонён.");
                return null;
            }
            if (doc.Entries.Count > MaxEntries)
            {
                AppLog.Write($"Переносимый кэш {path}: записей {doc.Entries.Count:N0} > {MaxEntries:N0} — импорт отклонён (кап).");
                return null;
            }

            var map = new Dictionary<string, PortableIndexEntry>(doc.Entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Entries)
            {
                if (string.IsNullOrWhiteSpace(e.RelPath)) continue;
                if (e.PartialHash is null && e.FullHash is null && e.PHash is null &&
                    e.PixelHash is null && e.AudioFp is null) continue; // без хэшей запись бесполезна
                map[e.RelPath] = e;
            }
            if (map.Count == 0) return null;

            AppLog.Write($"Переносимый кэш: прочитан {path} — записей {map.Count:N0} " +
                         $"(создан {header.CreatedAt:yyyy-MM-dd}, приложение {header.AppVersion}).");
            return new PortableIndexCache(header, map);
        }
        catch (Exception ex)
        {
            // Битый gzip/JSON, антивирус держит файл, носитель выдернули — кэша просто нет.
            AppLog.Error("PortableIndex.Import", ex);
            return null;
        }
    }

    /// <summary>
    /// Перенести хэши записи кэша в новую строку индекса. PartialVer/PHashVer — из заголовка
    /// (импорт гарантирует их равенство текущим), ScanState — по фактическому наличию признаков.
    /// VerifiedFullHash сознательно НЕ переносится: побайтовая сверка ЧУЖОЙ машины — не
    /// доказательство для этой; UpsertMetaBatch и так пишет verified_full_hash=NULL — здесь
    /// вторая, явная половина той же гарантии.
    /// </summary>
    public static void ApplyEntry(FileRecord record, PortableIndexEntry entry, PortableIndexHeader header)
    {
        record.PartialHash = entry.PartialHash;
        record.FullHash = entry.FullHash;
        record.PHash = entry.PHash;
        record.PixelHash = entry.PixelHash;
        record.AudioFp = entry.AudioFp;
        record.Sharpness = entry.Sharpness;
        record.PartialVer = header.PartialVer;
        record.PHashVer = header.PHashVer;
        record.ScanState =
            entry.PHash is not null || entry.AudioFp is not null ? ScanState.Perceptual :
            entry.FullHash is not null ? ScanState.Full :
            entry.PartialHash is not null ? ScanState.Partial : ScanState.Meta;
    }

    private static PortableIndexEntry ToEntry(FileRecord f) => new()
    {
        RelPath = f.RelPath,
        Size = f.Size,
        MtimeTicksUtc = f.Mtime.UtcTicks,
        // default — строка из БД до миграции «created»: пишем 0, при импорте строгая сверка
        // такой записи честно не пройдёт (кэш консервативен, послаблений нет).
        CreatedTicksUtc = f.Created == default ? 0 : f.Created.UtcTicks,
        PartialHash = f.PartialHash,
        FullHash = f.FullHash,
        PHash = f.PHash,
        PixelHash = f.PixelHash,
        AudioFp = f.AudioFp,
        Sharpness = f.Sharpness,
        VerifiedFullHash = f.VerifiedFullHash
    };

    private static JsonSerializerOptions CreateImportJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CappedEntryListConverter());
        return options;
    }

    /// <summary>
    /// Стандартный List-конвертер узнаёт о превышении лимита только после материализации всего JSON.
    /// Этот вариант прекращает разбор сразу на MaxEntries+1, поэтому подложный кэш не может создать
    /// неограниченный список объектов даже при маленьком сжатом файле.
    /// </summary>
    private sealed class CappedEntryListConverter : JsonConverter<List<PortableIndexEntry>>
    {
        public override List<PortableIndexEntry> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Entries must be an array.");

            var result = new List<PortableIndexEntry>(Math.Min(MaxEntries, 4096));
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return result;
                if (result.Count >= MaxEntries)
                    throw new JsonException($"Portable index contains more than {MaxEntries} entries.");
                var entry = JsonSerializer.Deserialize<PortableIndexEntry>(ref reader, options)
                            ?? throw new JsonException("Null portable index entry.");
                result.Add(entry);
            }
            throw new JsonException("Unexpected end of portable index entries.");
        }

        public override void Write(Utf8JsonWriter writer, List<PortableIndexEntry> value,
            JsonSerializerOptions options) => throw new NotSupportedException();
    }

    /// <summary>Ограничивает именно распакованный поток, защищая от gzip/decompression bomb.</summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public BoundedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _limit)
            {
                if (_inner.ReadByte() < 0) return 0;
                throw new InvalidDataException($"Decompressed portable index exceeds {_limit:N0} bytes.");
            }
            int allowed = (int)Math.Min(count, _limit - _read);
            int read = _inner.Read(buffer, offset, allowed);
            _read += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_read >= _limit)
            {
                if (_inner.ReadByte() < 0) return 0;
                throw new InvalidDataException($"Decompressed portable index exceeds {_limit:N0} bytes.");
            }
            int allowed = (int)Math.Min(buffer.Length, _limit - _read);
            int read = _inner.Read(buffer[..allowed]);
            _read += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
