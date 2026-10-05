using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

/// <summary>
/// Единственная точка входа отдельного Deduplicator.MftHelper.exe. Здесь нет App/Core/DI/БД/логов:
/// только аутентифицированный pipe, проверка тома и потоковое чтение FSCTL_ENUM_USN_DATA.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedMftHelperHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    public static int Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Native.IsElevated()) return 10;
            if (!TryParseArgs(args, out string pipeName, out byte[] token, out int parentPid)) return 11;
            MftHelperTrustResult trust = ElevatedMftHelperTrust.EvaluateParentForHelper(parentPid);
            if (!trust.IsTrusted) return 12;

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connect = new CancellationTokenSource(ConnectTimeout);
            pipe.ConnectAsync(connect.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
                serverPid != unchecked((uint)parentPid)) return 13;

            ElevatedMftProtocol.WriteFrameAsync(pipe, MftBrokerMessage.Hello,
                    ElevatedMftProtocol.CreateHello(Environment.ProcessId, token), connect.Token)
                .AsTask().GetAwaiter().GetResult();
            MftBrokerFrame ackFrame = ReadFrame(pipe, connect.Token, ConnectTimeout);
            if (ackFrame.Type != MftBrokerMessage.HelloAck) return 14;
            MftHello ack = ElevatedMftProtocol.ParseHello(ackFrame.Payload);
            if (ack.ProcessId != parentPid || !ElevatedMftProtocol.FixedTimeTokenEquals(ack.Token, token))
                return 15;

            return RunSession(pipe, parentPid);
        }
        catch (OperationCanceledException) { return 20; }
        catch { return 21; }
    }

    private static int RunSession(NamedPipeClientStream pipe, int parentPid)
    {
        using var hostCts = new CancellationTokenSource();
        var active = new ActiveRequestState();
        var commands = Channel.CreateBounded<MftBrokerFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Task reader = ReadCommandsAsync(pipe, commands.Writer, active, hostCts);
        Task parentMonitor = MonitorParentAsync(parentPid, hostCts);
        int requests = 0;
        try
        {
            while (!hostCts.IsCancellationRequested)
            {
                MftBrokerFrame command;
                try
                {
                    command = commands.Reader.ReadAsync(hostCts.Token).AsTask()
                        .WaitAsync(IdleTimeout, hostCts.Token).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    hostCts.Cancel();
                    break;
                }

                if (command.Type != MftBrokerMessage.EnumerateMft)
                    throw new InvalidDataException("Неожиданная команда helper.");
                if (++requests > ElevatedMftProtocol.MaxRequestsPerSession)
                    throw new InvalidDataException("Превышен лимит запросов helper.");

                MftEnumerateRequest request = ElevatedMftProtocol.ParseEnumerateRequest(command.Payload);
                using CancellationTokenSource requestCts = active.Begin(request.RequestId, hostCts.Token);
                try
                {
                    HandleEnumerate(pipe, request, requestCts.Token);
                }
                finally
                {
                    active.End(request.RequestId, requestCts);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (hostCts.IsCancellationRequested)
        {
            return 0;
        }
        catch
        {
            hostCts.Cancel();
            active.CancelAll();
            return 30;
        }
        finally
        {
            hostCts.Cancel();
            active.CancelAll();
            try { reader.GetAwaiter().GetResult(); } catch { }
            try { parentMonitor.GetAwaiter().GetResult(); } catch { }
        }
    }

    private static void HandleEnumerate(
        NamedPipeClientStream pipe, MftEnumerateRequest request, CancellationToken ct)
    {
        try
        {
            if (!MftVolumeProbe.TryProbe(request.Root, out string? beforeSerial, out string? beforeFs))
                throw new MftEnumerationUnavailableException(
                    BackendFallbackReason.VolumeNotReady, $"Том {request.Root} недоступен.");
            if (!string.Equals(beforeFs, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new MftEnumerationUnavailableException(
                    BackendFallbackReason.NonNtfs, $"Том {request.Root} не является NTFS.");
            if (!string.Equals(beforeSerial, request.ExpectedSerial, StringComparison.OrdinalIgnoreCase))
                throw new MftEnumerationUnavailableException(
                    BackendFallbackReason.VolumeNotReady, $"Том {request.Root} был заменён.");

            long sent = 0;
            var batch = new RawRecordBatchWriter(pipe, request.RequestId, ct);
            UsnEnumerator.ReadRawRecords(request.Root, record =>
            {
                ct.ThrowIfCancellationRequested();
                if (++sent > ElevatedMftProtocol.MaxRecordsPerRequest)
                    throw new InvalidDataException("MFT превысил допустимое число записей.");
                batch.Add(record);
            }, ct);
            batch.Flush();

            if (!MftVolumeProbe.TryProbe(request.Root, out string? afterSerial, out string? afterFs) ||
                !string.Equals(afterFs, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(afterSerial, request.ExpectedSerial, StringComparison.OrdinalIgnoreCase))
                throw new MftEnumerationUnavailableException(
                    BackendFallbackReason.VolumeNotReady, $"Том {request.Root} отключён или заменён во время чтения MFT.");

            ElevatedMftProtocol.WriteFrameAsync(pipe, MftBrokerMessage.Complete,
                    ElevatedMftProtocol.CreateComplete(request.RequestId, sent), ct)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (MftEnumerationUnavailableException ex)
        {
            SendError(pipe, request.RequestId, ex.Reason, ex.Message, ct);
        }
        catch (Exception ex)
        {
            SendError(pipe, request.RequestId, BackendFallbackReason.ApiError, ex.Message, ct);
        }
    }

    private static void SendError(
        NamedPipeClientStream pipe, int requestId, BackendFallbackReason reason,
        string message, CancellationToken ct)
    {
        string safe = string.IsNullOrWhiteSpace(message) ? "MFT-helper не смог прочитать том." : message;
        if (safe.Length > 2_000) safe = safe[..2_000];
        ElevatedMftProtocol.WriteFrameAsync(pipe, MftBrokerMessage.Error,
                ElevatedMftProtocol.CreateError(requestId, reason, safe), ct)
            .AsTask().GetAwaiter().GetResult();
    }

    private static async Task ReadCommandsAsync(
        NamedPipeClientStream pipe, ChannelWriter<MftBrokerFrame> writer,
        ActiveRequestState active, CancellationTokenSource hostCts)
    {
        Exception? failure = null;
        try
        {
            while (!hostCts.IsCancellationRequested)
            {
                MftBrokerFrame frame = await ElevatedMftProtocol.ReadFrameAsync(pipe, hostCts.Token)
                    .ConfigureAwait(false);
                switch (frame.Type)
                {
                    case MftBrokerMessage.EnumerateMft:
                        if (!writer.TryWrite(frame))
                            throw new InvalidDataException("Команды helper нельзя ставить в очередь.");
                        break;
                    case MftBrokerMessage.Cancel:
                        active.Cancel(ElevatedMftProtocol.ParseRequestId(frame.Payload));
                        break;
                    case MftBrokerMessage.Shutdown:
                        ElevatedMftProtocol.EnsureEmpty(frame.Payload);
                        active.CancelAll();
                        hostCts.Cancel();
                        break;
                    default:
                        throw new InvalidDataException("Недопустимая команда helper.");
                }
            }
        }
        catch (OperationCanceledException) when (hostCts.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            active.CancelAll();
            if (!hostCts.IsCancellationRequested) hostCts.Cancel();
            writer.TryComplete(failure);
        }
    }

    private static async Task MonitorParentAsync(int parentPid, CancellationTokenSource hostCts)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(hostCts.Token).ConfigureAwait(false);
            if (!hostCts.IsCancellationRequested) hostCts.Cancel();
        }
        catch (OperationCanceledException) when (hostCts.IsCancellationRequested) { }
        catch
        {
            if (!hostCts.IsCancellationRequested) hostCts.Cancel();
        }
    }

    private static MftBrokerFrame ReadFrame(Stream pipe, CancellationToken ct, TimeSpan timeout)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        return ElevatedMftProtocol.ReadFrameAsync(pipe, bounded.Token).AsTask().GetAwaiter().GetResult();
    }

    private static bool TryParseArgs(
        string[] args, out string pipeName, out byte[] token, out int parentPid)
    {
        pipeName = "";
        token = Array.Empty<byte>();
        parentPid = 0;
        if (args.Length != 6) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!values.TryAdd(args[i], args[i + 1])) return false;
        }
        if (!values.TryGetValue("--pipe", out string? parsedPipeName) ||
            !ElevatedMftProtocol.IsValidPipeName(parsedPipeName) ||
            !values.TryGetValue("--token", out string? encodedToken) ||
            !values.TryGetValue("--parent", out string? encodedParent) ||
            !int.TryParse(encodedParent, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out parentPid) ||
            parentPid <= 0 || parentPid == Environment.ProcessId) return false;
        pipeName = parsedPipeName;
        try { token = ElevatedMftProtocol.DecodeToken(encodedToken); }
        catch { return false; }
        return true;
    }

    private sealed class RawRecordBatchWriter
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly int _requestId;
        private readonly CancellationToken _ct;
        private readonly List<UsnEnumerator.RawRec> _records = new(ElevatedMftProtocol.MaxRecordsPerBatch);
        private int _estimatedPayloadBytes = 8;

        public RawRecordBatchWriter(NamedPipeClientStream pipe, int requestId, CancellationToken ct)
        {
            _pipe = pipe;
            _requestId = requestId;
            _ct = ct;
        }

        public void Add(UsnEnumerator.RawRec record)
        {
            if (!ElevatedMftProtocol.IsSafeMftName(record.Name))
                throw new InvalidDataException("MFT вернул имя, которое нельзя безопасно передать.");
            int recordBytes = checked(24 + Encoding.UTF8.GetByteCount(record.Name));
            if (_records.Count > 0 &&
                (_records.Count >= ElevatedMftProtocol.MaxRecordsPerBatch ||
                 _estimatedPayloadBytes + recordBytes + 1 > ElevatedMftProtocol.MaxFrameBytes))
                Flush();
            _records.Add(record);
            _estimatedPayloadBytes = checked(_estimatedPayloadBytes + recordBytes);
        }

        public void Flush()
        {
            if (_records.Count == 0) return;
            byte[] payload = ElevatedMftProtocol.CreateRawRecords(_requestId, _records);
            ElevatedMftProtocol.WriteFrameAsync(_pipe, MftBrokerMessage.RawRecords, payload, _ct)
                .AsTask().GetAwaiter().GetResult();
            _records.Clear();
            _estimatedPayloadBytes = 8;
        }
    }

    private sealed class ActiveRequestState
    {
        private readonly object _sync = new();
        private int _requestId;
        private CancellationTokenSource? _request;

        public CancellationTokenSource Begin(int requestId, CancellationToken hostToken)
        {
            lock (_sync)
            {
                if (_request is not null) throw new InvalidOperationException("Helper уже выполняет запрос.");
                _requestId = requestId;
                _request = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
                return _request;
            }
        }

        public void Cancel(int requestId)
        {
            lock (_sync)
            {
                if (_requestId == requestId) _request?.Cancel();
            }
        }

        public void End(int requestId, CancellationTokenSource request)
        {
            lock (_sync)
            {
                if (_requestId == requestId && ReferenceEquals(_request, request))
                {
                    _request = null;
                    _requestId = 0;
                }
            }
        }

        public void CancelAll()
        {
            lock (_sync) _request?.Cancel();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
