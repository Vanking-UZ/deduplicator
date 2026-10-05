using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FastDedup.Engine;

internal enum MftBrokerMessage : byte
{
    Hello = 1,
    HelloAck = 2,
    EnumerateMft = 3,
    RawRecords = 4,
    Complete = 5,
    Error = 6,
    Cancel = 7,
    Shutdown = 8
}

internal readonly record struct MftBrokerFrame(MftBrokerMessage Type, byte[] Payload);
internal readonly record struct MftHello(int ProcessId, byte[] Token);
internal readonly record struct MftEnumerateRequest(int RequestId, string Root, string ExpectedSerial);
internal readonly record struct MftComplete(int RequestId, long RecordCount);
internal readonly record struct MftError(int RequestId, BackendFallbackReason Reason, string Message);

/// <summary>
/// Ограниченный бинарный протокол между обычным процессом и отдельным MFT-helper. В протоколе нет
/// JSON/типовой десериализации: размер каждой рамки и строки проверяется до выделения памяти.
/// </summary>
internal static class ElevatedMftProtocol
{
    internal const int Version = 1;
    internal const int TokenBytes = 32;
    internal const int MaxFrameBytes = 256 * 1024;
    internal const int MaxRecordsPerBatch = 4096;
    internal const long MaxRecordsPerRequest = 50_000_000;
    internal const int MaxRequestsPerSession = 64;
    internal const int MaxErrorBytes = 8 * 1024;
    internal const int MaxNameBytes = 4 * 1024;
    internal const string PipePrefix = "Deduplicator.Mft.";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] CreateToken() => RandomNumberGenerator.GetBytes(TokenBytes);

    internal static string EncodeToken(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenBytes) throw new ArgumentException("Неверная длина IPC-токена.", nameof(token));
        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static byte[] DecodeToken(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 64)
            throw new InvalidDataException("Неверный IPC-токен.");
        if (encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Неверный IPC-токен.");

        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        byte[] token;
        try { token = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new InvalidDataException("Неверный IPC-токен.", ex); }
        if (token.Length != TokenBytes) throw new InvalidDataException("Неверная длина IPC-токена.");
        return token;
    }

    internal static bool FixedTimeTokenEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.Length == TokenBytes && right.Length == TokenBytes &&
           CryptographicOperations.FixedTimeEquals(left, right);

    internal static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    internal static bool IsValidPipeName(string? name)
    {
        if (name is null || name.Length != PipePrefix.Length + 32 ||
            !name.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
        return name.AsSpan(PipePrefix.Length).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;
    }

    internal static string NormalizeWholeVolumeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length != 3 ||
            !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new InvalidDataException("Helper принимает только локальный корень тома вида C:\\.");
        return char.ToUpperInvariant(root[0]) + @":\";
    }

    internal static string NormalizeSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) throw new InvalidDataException("Не задан серийный номер тома.");
        string normalized = serial.Trim().ToUpperInvariant();
        if (normalized.Length != 9 || normalized[4] != '-' ||
            normalized.Where((_, index) => index != 4).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Неверный формат серийного номера тома.");
        return normalized;
    }

    internal static bool IsPathWithinRoot(string root, string path)
    {
        try
        {
            string normalizedRoot = NormalizeWholeVolumeRoot(root);
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > 32_767)
                return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                return false;
            if (path.AsSpan(2).IndexOf(':') >= 0) return false; // ADS / второй drive-prefix

            string full = Path.GetFullPath(path);
            return full.Length > normalizedRoot.Length &&
                   full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsSafeMftName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or "..") return false;
        foreach (char c in name)
        {
            if (c < 32 || c is '\0' or '\\' or '/' or ':' or '"' or '<' or '>' or '|' or '?' or '*')
                return false;
        }
        return StrictUtf8.GetByteCount(name) <= MaxNameBytes;
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream, MftBrokerMessage type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int frameLength = checked(payload.Length + 1);
        if (frameLength > MaxFrameBytes) throw new InvalidDataException("IPC-рамка превышает допустимый размер.");

        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), frameLength);
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async ValueTask<MftBrokerFrame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            byte[] lengthBytes = new byte[4];
            await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
            int frameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (frameLength < 1 || frameLength > MaxFrameBytes)
                throw new InvalidDataException("Некорректный размер IPC-рамки.");

            byte[] frame = GC.AllocateUninitializedArray<byte>(frameLength);
            await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
            var type = (MftBrokerMessage)frame[0];
            if (!Enum.IsDefined(type)) throw new InvalidDataException("Неизвестный тип IPC-рамки.");
            return new MftBrokerFrame(type, frame.AsSpan(1).ToArray());
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("IPC-поток завершился до финальной рамки.", ex);
        }
    }

    internal static byte[] CreateHello(int processId, ReadOnlySpan<byte> token)
    {
        using var w = new PayloadWriter();
        w.Write(Version);
        w.Write(processId);
        w.WriteFixed(token, TokenBytes);
        return w.ToArray();
    }

    internal static MftHello ParseHello(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException("Несовместимая версия IPC-протокола.");
        int processId = r.ReadInt32();
        if (processId <= 0) throw new InvalidDataException("Некорректный PID в IPC-приветствии.");
        byte[] token = r.ReadFixed(TokenBytes);
        r.EnsureEnd();
        return new MftHello(processId, token);
    }

    internal static byte[] CreateEnumerateRequest(int requestId, string root, string expectedSerial)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        using var w = new PayloadWriter();
        w.Write(requestId);
        w.WriteString(NormalizeWholeVolumeRoot(root), 16);
        w.WriteString(NormalizeSerial(expectedSerial), 32);
        return w.ToArray();
    }

    internal static MftEnumerateRequest ParseEnumerateRequest(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        int requestId = r.ReadInt32();
        if (requestId <= 0) throw new InvalidDataException("Некорректный RequestId.");
        string root = NormalizeWholeVolumeRoot(r.ReadString(16));
        string serial = NormalizeSerial(r.ReadString(32));
        r.EnsureEnd();
        return new MftEnumerateRequest(requestId, root, serial);
    }

    internal static byte[] CreateRawRecords(int requestId, IReadOnlyList<UsnEnumerator.RawRec> records)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count is < 1 or > MaxRecordsPerBatch)
            throw new InvalidDataException("Некорректный размер пачки MFT.");

        using var w = new PayloadWriter();
        w.Write(requestId);
        w.Write(records.Count);
        foreach (var rec in records)
        {
            if (!IsSafeMftName(rec.Name)) throw new InvalidDataException("MFT вернул небезопасное имя.");
            w.Write(rec.Frn);
            w.Write(rec.Parent);
            w.Write(rec.Attrs);
            w.WriteString(rec.Name, MaxNameBytes);
        }
        byte[] payload = w.ToArray();
        if (payload.Length + 1 > MaxFrameBytes)
            throw new InvalidDataException("Пачка MFT превышает размер IPC-рамки.");
        return payload;
    }

    internal static (int RequestId, List<UsnEnumerator.RawRec> Records) ParseRawRecords(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        int requestId = r.ReadInt32();
        int count = r.ReadInt32();
        if (requestId <= 0 || count is < 1 or > MaxRecordsPerBatch)
            throw new InvalidDataException("Некорректная пачка MFT.");
        // Минимум 8+8+4+4 байта на запись. Проверка до List исключает завышенный count.
        if (r.Remaining < count * 24L) throw new InvalidDataException("Усечённая пачка MFT.");

        var records = new List<UsnEnumerator.RawRec>(count);
        for (int i = 0; i < count; i++)
        {
            ulong frn = r.ReadUInt64();
            ulong parent = r.ReadUInt64();
            uint attrs = r.ReadUInt32();
            string name = r.ReadString(MaxNameBytes);
            if (!IsSafeMftName(name)) throw new InvalidDataException("MFT вернул небезопасное имя.");
            records.Add(new UsnEnumerator.RawRec(frn, parent, attrs, name));
        }
        r.EnsureEnd();
        return (requestId, records);
    }

    internal static byte[] CreateComplete(int requestId, long recordCount)
    {
        if (requestId <= 0 || recordCount < 0 || recordCount > MaxRecordsPerRequest)
            throw new InvalidDataException("Некорректный итог MFT.");
        using var w = new PayloadWriter();
        w.Write(requestId);
        w.Write(recordCount);
        return w.ToArray();
    }

    internal static MftComplete ParseComplete(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        var result = new MftComplete(r.ReadInt32(), r.ReadInt64());
        r.EnsureEnd();
        if (result.RequestId <= 0 || result.RecordCount < 0 || result.RecordCount > MaxRecordsPerRequest)
            throw new InvalidDataException("Некорректный итог MFT.");
        return result;
    }

    internal static byte[] CreateError(int requestId, BackendFallbackReason reason, string message)
    {
        if (requestId < 0 || reason == BackendFallbackReason.None || !Enum.IsDefined(reason))
            throw new InvalidDataException("Некорректная ошибка helper.");
        using var w = new PayloadWriter();
        w.Write(requestId);
        w.Write((int)reason);
        w.WriteString(string.IsNullOrWhiteSpace(message) ? "MFT-helper недоступен." : message, MaxErrorBytes);
        return w.ToArray();
    }

    internal static MftError ParseError(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        int requestId = r.ReadInt32();
        var reason = (BackendFallbackReason)r.ReadInt32();
        string message = r.ReadString(MaxErrorBytes);
        r.EnsureEnd();
        if (requestId < 0 || reason == BackendFallbackReason.None || !Enum.IsDefined(reason))
            throw new InvalidDataException("Некорректная ошибка helper.");
        return new MftError(requestId, reason, message);
    }

    internal static byte[] CreateRequestId(int requestId)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        using var w = new PayloadWriter();
        w.Write(requestId);
        return w.ToArray();
    }

    internal static int ParseRequestId(ReadOnlySpan<byte> payload)
    {
        using var r = new PayloadReader(payload);
        int requestId = r.ReadInt32();
        r.EnsureEnd();
        if (requestId <= 0) throw new InvalidDataException("Некорректный RequestId.");
        return requestId;
    }

    internal static void EnsureEmpty(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty) throw new InvalidDataException("IPC-рамка должна быть пустой.");
    }

    private sealed class PayloadWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public PayloadWriter() => _writer = new BinaryWriter(_stream, StrictUtf8, leaveOpen: true);
        public void Write(int value) => _writer.Write(value);
        public void Write(long value) => _writer.Write(value);
        public void Write(uint value) => _writer.Write(value);
        public void Write(ulong value) => _writer.Write(value);

        public void WriteFixed(ReadOnlySpan<byte> value, int expectedLength)
        {
            if (value.Length != expectedLength) throw new InvalidDataException("Неверная длина бинарного поля.");
            _writer.Write(value);
        }

        public void WriteString(string value, int maxBytes)
        {
            byte[] bytes = StrictUtf8.GetBytes(value ?? throw new InvalidDataException("Строка IPC равна null."));
            if (bytes.Length > maxBytes) throw new InvalidDataException("Строка IPC превышает лимит.");
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }

        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class PayloadReader : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public PayloadReader(ReadOnlySpan<byte> payload)
        {
            _stream = new MemoryStream(payload.ToArray(), writable: false);
            _reader = new BinaryReader(_stream, StrictUtf8, leaveOpen: true);
        }

        public long Remaining => _stream.Length - _stream.Position;

        public int ReadInt32() => Read(() => _reader.ReadInt32());
        public long ReadInt64() => Read(() => _reader.ReadInt64());
        public uint ReadUInt32() => Read(() => _reader.ReadUInt32());
        public ulong ReadUInt64() => Read(() => _reader.ReadUInt64());

        public byte[] ReadFixed(int length)
        {
            if (length < 0 || Remaining < length) throw new InvalidDataException("Усечённое поле IPC.");
            byte[] bytes = _reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("Усечённое поле IPC.");
            return bytes;
        }

        public string ReadString(int maxBytes)
        {
            int length = ReadInt32();
            if (length < 0 || length > maxBytes || Remaining < length)
                throw new InvalidDataException("Некорректная длина строки IPC.");
            byte[] bytes = ReadFixed(length);
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Строка IPC не является UTF-8.", ex); }
        }

        public void EnsureEnd()
        {
            if (Remaining != 0) throw new InvalidDataException("Лишние данные в IPC-рамке.");
        }

        private static T Read<T>(Func<T> read)
        {
            try { return read(); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Усечённая IPC-рамка.", ex); }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }
}
