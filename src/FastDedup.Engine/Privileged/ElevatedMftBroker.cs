using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

public readonly record struct ElevatedMftAllowedRoot(string Root, string ExpectedSerial);

public interface IElevatedMftBroker : IDisposable
{
    bool CanEnumerate(string root);
    string? FailureReason { get; }
    IEnumerable<string> Enumerate(
        string root, bool includeReparsePoints, bool skipHiddenSystem,
        Action<string, bool>? onReparseSkipped, CancellationToken ct);
}

public interface IElevatedMftBrokerFactory
{
    IElevatedMftBroker Create(IEnumerable<ElevatedMftAllowedRoot> allowedRoots);
}

public sealed class ElevatedMftBrokerFactory : IElevatedMftBrokerFactory
{
    public IElevatedMftBroker Create(IEnumerable<ElevatedMftAllowedRoot> allowedRoots)
        => new ElevatedMftBrokerSession(allowedRoots);
}

public sealed class ElevatedMftBrokerUnavailableException : IOException
{
    public BackendFallbackReason Reason { get; }

    public ElevatedMftBrokerUnavailableException(BackendFallbackReason reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason == BackendFallbackReason.None
            ? BackendFallbackReason.ElevatedHelperUnavailable
            : reason;
    }
}

/// <summary>
/// Ленивый клиент отдельного повышенного MFT-helper. Конструктор не показывает UAC; попытка запуска
/// происходит только при первом полном сканировании разрешённого NTFS-тома. Один объект = не более
/// одной UAC-попытки и одного helper-процесса на скан.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedMftBrokerSession : IElevatedMftBroker, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ResponseIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, string> _allowedRoots;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly MftHelperTrustResult _trust;

    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private byte[]? _token;
    private bool _launchAttempted;
    private bool _disposed;
    private int _nextRequestId;
    private string? _failureReason;

    public ElevatedMftBrokerSession(IEnumerable<ElevatedMftAllowedRoot> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _allowedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var allowed in allowedRoots)
        {
            string root = ElevatedMftProtocol.NormalizeWholeVolumeRoot(allowed.Root);
            string serial = ElevatedMftProtocol.NormalizeSerial(allowed.ExpectedSerial);
            if (!_allowedRoots.TryAdd(root, serial) &&
                !string.Equals(_allowedRoots[root], serial, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Для корня {root} заданы разные серийные номера.", nameof(allowedRoots));
        }
        if (_allowedRoots.Count > 26) throw new ArgumentException("Слишком много разрешённых томов.", nameof(allowedRoots));
        _trust = ElevatedMftHelperTrust.EvaluateForMainProcess();
    }

    public string? FailureReason
    {
        get { lock (_stateLock) return _failureReason ?? (_trust.IsTrusted ? null : _trust.Reason); }
    }

    public bool CanEnumerate(string root)
    {
        try
        {
            string normalized = ElevatedMftProtocol.NormalizeWholeVolumeRoot(root);
            lock (_stateLock) return !_disposed && _allowedRoots.ContainsKey(normalized);
        }
        catch { return false; }
    }

    public IEnumerable<string> Enumerate(
        string root, bool includeReparsePoints, bool skipHiddenSystem,
        Action<string, bool>? onReparseSkipped, CancellationToken ct)
    {
        string normalizedRoot = ElevatedMftProtocol.NormalizeWholeVolumeRoot(root);
        if (!_allowedRoots.TryGetValue(normalizedRoot, out string? expectedSerial))
            throw new ElevatedMftBrokerUnavailableException(
                BackendFallbackReason.ElevatedHelperUnavailable,
                $"Том {normalizedRoot} не входит в разрешённую область этого сканирования.");

        return EnumerateCore(normalizedRoot, expectedSerial, includeReparsePoints,
            skipHiddenSystem, onReparseSkipped, ct);
    }

    private IEnumerable<string> EnumerateCore(
        string root, string expectedSerial, bool includeReparsePoints, bool skipHiddenSystem,
        Action<string, bool>? onReparseSkipped, CancellationToken ct)
    {
        _requestGate.Wait(ct);
        int requestId = 0;
        bool protocolComplete = false;
        try
        {
            EnsureConnected(ct);
            requestId = checked(++_nextRequestId);
            if (requestId > ElevatedMftProtocol.MaxRequestsPerSession)
                throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                    "Превышен лимит запросов к MFT-helper в одном сканировании.");

            WriteFrame(MftBrokerMessage.EnumerateMft,
                ElevatedMftProtocol.CreateEnumerateRequest(requestId, root, expectedSerial), ct);
            Dictionary<ulong, UsnEnumerator.Rec> map = ReceiveCompleteMap(requestId, root, expectedSerial, ct);
            protocolComplete = true;

            var options = new ScanOptions
            {
                Roots = new[] { root },
                IncludeReparsePoints = includeReparsePoints,
                SkipHiddenSystem = skipHiddenSystem,
                OnReparseSkipped = onReparseSkipped
            };
            foreach (string path in UsnEnumerator.BuildPaths(map, root[..2], root, options, ct))
            {
                if (!ElevatedMftProtocol.IsPathWithinRoot(root, path))
                    throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                        "MFT-helper вернул путь вне выбранного тома.");
                yield return path;
            }
        }
        finally
        {
            if (!protocolComplete && requestId > 0) AbortRequest(requestId);
            _requestGate.Release();
        }
    }

    private Dictionary<ulong, UsnEnumerator.Rec> ReceiveCompleteMap(
        int requestId, string root, string expectedSerial, CancellationToken ct)
    {
        try { return ReceiveCompleteMapCore(requestId, root, expectedSerial, ct); }
        catch (OperationCanceledException) { throw; }
        catch (ElevatedMftBrokerUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                "Ответ MFT-helper не прошёл проверку: " + ex.Message, ex);
        }
    }

    private Dictionary<ulong, UsnEnumerator.Rec> ReceiveCompleteMapCore(
        int requestId, string root, string expectedSerial, CancellationToken ct)
    {
        var map = new Dictionary<ulong, UsnEnumerator.Rec>(capacity: 65_536);
        long received = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            MftBrokerFrame frame = ReadFrame(ct);
            switch (frame.Type)
            {
                case MftBrokerMessage.RawRecords:
                {
                    var batch = ElevatedMftProtocol.ParseRawRecords(frame.Payload);
                    if (batch.RequestId != requestId)
                        throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                            "MFT-helper ответил с чужим RequestId.");
                    received = checked(received + batch.Records.Count);
                    if (received > ElevatedMftProtocol.MaxRecordsPerRequest)
                        throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                            "MFT-helper превысил лимит записей.");
                    foreach (var rec in batch.Records)
                        map[rec.Frn] = new UsnEnumerator.Rec(rec.Parent, rec.Attrs, rec.Name);
                    break;
                }
                case MftBrokerMessage.Complete:
                {
                    MftComplete complete = ElevatedMftProtocol.ParseComplete(frame.Payload);
                    if (complete.RequestId != requestId || complete.RecordCount != received)
                        throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                            "MFT-helper завершил неполный поток записей.");
                    if (!MftVolumeProbe.TryProbe(root, out string? actualSerial, out string? fileSystem) ||
                        !string.Equals(actualSerial, expectedSerial, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                        throw Fail(BackendFallbackReason.VolumeNotReady,
                            $"Том {root} отключён или заменён во время MFT-чтения.");
                    return map;
                }
                case MftBrokerMessage.Error:
                {
                    MftError error = ElevatedMftProtocol.ParseError(frame.Payload);
                    if (error.RequestId != requestId)
                        throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                            "MFT-helper прислал ошибку для чужого RequestId.");
                    throw Fail(error.Reason, error.Message);
                }
                default:
                    throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                        $"Неожиданный ответ MFT-helper: {frame.Type}.");
            }
        }
    }

    private void EnsureConnected(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ElevatedMftBrokerSession));
            if (_pipe is { IsConnected: true }) return;
            if (_launchAttempted)
                throw new ElevatedMftBrokerUnavailableException(
                    BackendFallbackReason.ElevatedHelperUnavailable,
                    _failureReason ?? "MFT-helper уже оказался недоступен в этом сканировании.");
            _launchAttempted = true;
        }

        if (!_trust.IsTrusted || string.IsNullOrWhiteSpace(_trust.HelperPath))
            throw Fail(BackendFallbackReason.ElevatedHelperUnavailable, _trust.Reason);

        string pipeName = ElevatedMftProtocol.NewPipeName();
        byte[] token = ElevatedMftProtocol.CreateToken();
        NamedPipeServerStream? server = null;
        Process? helper = null;
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                64 * 1024, 64 * 1024);

            var psi = new ProcessStartInfo
            {
                FileName = _trust.HelperPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(_trust.HelperPath)!,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add("--token");
            psi.ArgumentList.Add(ElevatedMftProtocol.EncodeToken(token));
            psi.ArgumentList.Add("--parent");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(StartupTimeout);
            helper = StartHelperBounded(psi, startup.Token);
            WaitForConnectionOrExit(server, helper, startup.Token);

            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid) ||
                clientPid != unchecked((uint)helper.Id))
                throw new InvalidDataException("PID клиента IPC не совпал с запущенным MFT-helper.");
            string? actualImage = ElevatedMftHelperTrust.TryGetProcessImagePath(helper.Id);
            if (!string.Equals(Path.GetFullPath(actualImage ?? ""), Path.GetFullPath(_trust.HelperPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("К IPC подключился другой исполняемый файл.");

            MftBrokerFrame helloFrame = ReadFrame(server, startup.Token, StartupTimeout);
            if (helloFrame.Type != MftBrokerMessage.Hello)
                throw new InvalidDataException("MFT-helper не прислал IPC-приветствие.");
            MftHello hello = ElevatedMftProtocol.ParseHello(helloFrame.Payload);
            if (hello.ProcessId != helper.Id || !ElevatedMftProtocol.FixedTimeTokenEquals(hello.Token, token))
                throw new InvalidDataException("IPC-приветствие MFT-helper не прошло проверку.");

            ElevatedMftProtocol.WriteFrameAsync(server, MftBrokerMessage.HelloAck,
                    ElevatedMftProtocol.CreateHello(Environment.ProcessId, token), startup.Token)
                .AsTask().GetAwaiter().GetResult();

            lock (_stateLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ElevatedMftBrokerSession));
                _pipe = server;
                _helper = helper;
                _token = token;
                server = null;
                helper = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                "Подтверждение Windows отменено — проверка продолжена в обычном режиме.", ex);
        }
        catch (Exception ex) when (ex is not ElevatedMftBrokerUnavailableException)
        {
            throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                "Не удалось запустить защищённый MFT-helper: " + ex.Message, ex);
        }
        finally
        {
            server?.Dispose();
            StopProcess(helper);
        }
    }

    private static Process StartHelperBounded(ProcessStartInfo psi, CancellationToken ct)
    {
        Task<Process?> startTask = Task.Run(() => Process.Start(psi));
        try
        {
            return startTask.WaitAsync(ct).GetAwaiter().GetResult()
                   ?? throw new IOException("Windows не вернула процесс MFT-helper.");
        }
        catch
        {
            _ = startTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) StopProcess(t.Result);
                _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static void WaitForConnectionOrExit(
        NamedPipeServerStream server, Process helper, CancellationToken ct)
    {
        Task connect = server.WaitForConnectionAsync(ct);
        Task exited = helper.WaitForExitAsync(ct);
        Task first = Task.WhenAny(connect, exited).GetAwaiter().GetResult();
        if (first == exited && !connect.IsCompleted)
            throw new IOException($"MFT-helper завершился до подключения (код {helper.ExitCode}).");
        connect.GetAwaiter().GetResult();
    }

    private MftBrokerFrame ReadFrame(CancellationToken ct)
    {
        NamedPipeServerStream pipe = GetConnectedPipe();
        return ReadFrame(pipe, ct, ResponseIdleTimeout);
    }

    private static MftBrokerFrame ReadFrame(Stream stream, CancellationToken ct, TimeSpan timeout)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(timeout);
        try
        {
            return ElevatedMftProtocol.ReadFrameAsync(stream, idle.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("MFT-helper не ответил вовремя.");
        }
    }

    private void WriteFrame(MftBrokerMessage type, byte[] payload, CancellationToken ct)
    {
        try
        {
            ElevatedMftProtocol.WriteFrameAsync(GetConnectedPipe(), type, payload, ct)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw Fail(BackendFallbackReason.ElevatedHelperUnavailable,
                "Связь с MFT-helper прервана: " + ex.Message, ex);
        }
    }

    private NamedPipeServerStream GetConnectedPipe()
    {
        lock (_stateLock)
        {
            if (_pipe is { IsConnected: true }) return _pipe;
            throw new ElevatedMftBrokerUnavailableException(
                BackendFallbackReason.ElevatedHelperUnavailable,
                _failureReason ?? "MFT-helper не подключён.");
        }
    }

    private ElevatedMftBrokerUnavailableException Fail(
        BackendFallbackReason reason, string message, Exception? inner = null)
    {
        MarkFailed(message);
        return new ElevatedMftBrokerUnavailableException(reason, message, inner);
    }

    private void AbortRequest(int requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            NamedPipeServerStream? pipe;
            lock (_stateLock) pipe = _pipe;
            if (pipe is { IsConnected: true })
                ElevatedMftProtocol.WriteFrameAsync(pipe, MftBrokerMessage.Cancel,
                        ElevatedMftProtocol.CreateRequestId(requestId), timeout.Token)
                    .AsTask().GetAwaiter().GetResult();
        }
        catch { /* закрытие pipe ниже всё равно отменит helper */ }
        MarkFailed("Запрос MFT-helper был прерван; повторный UAC в этом сканировании не выполняется.");
    }

    private void MarkFailed(string reason)
    {
        NamedPipeServerStream? pipe;
        Process? helper;
        lock (_stateLock)
        {
            _failureReason ??= reason;
            pipe = _pipe;
            helper = _helper;
            _pipe = null;
            _helper = null;
            _token = null;
        }
        try { pipe?.Dispose(); } catch { }
        StopProcess(helper);
    }

    public void Dispose()
    {
        NamedPipeServerStream? pipe;
        Process? helper;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            pipe = _pipe;
            helper = _helper;
            _pipe = null;
            _helper = null;
            _token = null;
        }

        if (pipe is { IsConnected: true })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                ElevatedMftProtocol.WriteFrameAsync(pipe, MftBrokerMessage.Shutdown,
                    ReadOnlyMemory<byte>.Empty, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch { }
        }
        try { pipe?.Dispose(); } catch { }
        if (helper is not null)
        {
            try { if (!helper.WaitForExit((int)ShutdownTimeout.TotalMilliseconds)) helper.Kill(true); }
            catch { }
            helper.Dispose();
        }
        _requestGate.Dispose();
    }

    private static void StopProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        finally { process.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

internal static class MftVolumeProbe
{
    internal static bool TryProbe(string root, out string? serial, out string? fileSystem)
    {
        serial = null;
        fileSystem = null;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string normalized = ElevatedMftProtocol.NormalizeWholeVolumeRoot(root);
            var drive = new DriveInfo(normalized);
            if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
                return false;
            var volumeName = new StringBuilder(261);
            var fsName = new StringBuilder(261);
            if (!GetVolumeInformation(normalized, volumeName, volumeName.Capacity, out uint rawSerial,
                    out _, out _, fsName, fsName.Capacity)) return false;
            serial = $"{(rawSerial >> 16) & 0xFFFF:X4}-{rawSerial & 0xFFFF:X4}";
            fileSystem = fsName.ToString();
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
