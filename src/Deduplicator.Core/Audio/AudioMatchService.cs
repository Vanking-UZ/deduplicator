using System.Diagnostics;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Audio;

public interface IAudioMatchService
{
    /// <summary>Доступен ли fpcalc.exe (иначе аудио-сверка пропускается).</summary>
    bool IsAvailable { get; }

    /// <summary>Путь к используемому fpcalc (для диагностики/настроек).</summary>
    string? FpcalcPath { get; }

    /// <summary>Переопределить путь к fpcalc «на лету» (после изменения в настройках), без перезапуска.</summary>
    void Reconfigure(string? configuredPath);

    /// <summary>Аудио-отпечаток Chromaprint в упакованном виде. null — если не удалось.</summary>
    byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default);
}

/// <summary>Интеграция с Chromaprint через внешний fpcalc.exe (AcoustID).</summary>
public sealed class AudioMatchService : IAudioMatchService
{
    internal static readonly TimeSpan DefaultPerFileTimeout = TimeSpan.FromMinutes(2);

    private string? _fpcalcPath;
    private readonly TimeSpan _perFileTimeout;
    private readonly Func<string, string, ProcessStartInfo> _startInfoFactory;

    public AudioMatchService(string? configuredPath = null, TimeSpan? perFileTimeout = null)
        : this(configuredPath, perFileTimeout ?? DefaultPerFileTimeout, CreateStartInfo)
    {
    }

    internal AudioMatchService(string? configuredPath, TimeSpan perFileTimeout,
        Func<string, string, ProcessStartInfo> startInfoFactory)
    {
        if (perFileTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perFileTimeout));

        _fpcalcPath = ResolveFpcalc(configuredPath);
        _perFileTimeout = perFileTimeout;
        _startInfoFactory = startInfoFactory ?? throw new ArgumentNullException(nameof(startInfoFactory));
    }

    public bool IsAvailable => _fpcalcPath is not null && File.Exists(_fpcalcPath);
    public string? FpcalcPath => _fpcalcPath;

    public void Reconfigure(string? configuredPath) => _fpcalcPath = ResolveFpcalc(configuredPath);

    public byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        ct.ThrowIfCancellationRequested();

        Process? process = null;
        try
        {
            var psi = _startInfoFactory(_fpcalcPath!, fullPath);
            process = Process.Start(psi);
            if (process is null)
            {
                AppLog.Write("WARN AudioMatch fpcalc could not be started");
                return null;
            }

            using var timeout = new CancellationTokenSource(_perFileTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            // Читаем stdout и stderr параллельно, чтобы не было взаимоблокировки при
            // переполнении буфера одного из потоков. В памяти держим только ограниченный префикс:
            // повреждённый/подменённый декодер не должен раздувать процесс гигабайтами вывода.
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 4 * 1024 * 1024, linked.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
            try
            {
                process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TerminateProcessTree(process);
                DrainAfterTermination(stdoutTask, stderrTask);
                AppLog.Write($"WARN AudioMatch fpcalc timed out after {_perFileTimeout.TotalSeconds:N0} seconds");
                return null;
            }
            catch (OperationCanceledException)
            {
                TerminateProcessTree(process);
                DrainAfterTermination(stdoutTask, stderrTask);
                AppLog.Write("INFO AudioMatch fpcalc cancelled; process tree terminated");
                throw;
            }

            string output = stdoutTask.GetAwaiter().GetResult();
            string error = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                AppLog.Write($"WARN AudioMatch fpcalc exited with code {process.ExitCode}: {ForLog(error)}");
                return null;
            }
            var fingerprint = ParseFingerprint(output);
            if (fingerprint is null)
                AppLog.Write("WARN AudioMatch fpcalc returned no valid fingerprint");
            return fingerprint;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("AudioMatch.ComputeFingerprint", ex);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string fullPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-raw");
        psi.ArgumentList.Add("-length");
        psi.ArgumentList.Add("120");
        psi.ArgumentList.Add(fullPath);
        return psi;
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("AudioMatch.TerminateFpcalc", ex);
        }

        try { process.WaitForExit(5_000); }
        catch { /* best effort: the original timeout/cancellation must still complete */ }
    }

    private static void DrainAfterTermination(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try { Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5)); }
        catch { /* readers are diagnostic only after the process has been terminated */ }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int retainedCharacters,
        CancellationToken ct)
    {
        var output = new System.Text.StringBuilder(Math.Min(retainedCharacters, 16 * 1024));
        var buffer = new char[8 * 1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return output.ToString();
            int keep = Math.Min(read, retainedCharacters - output.Length);
            if (keep > 0) output.Append(buffer, 0, keep);
            // После достижения лимита поток всё равно дренируется, иначе дочерний процесс может
            // заблокироваться на заполненном pipe и никогда не завершиться.
        }
    }

    private static string ForLog(string value)
    {
        const int maxLength = 512;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }

    private static byte[]? ParseFingerprint(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("FINGERPRINT=", StringComparison.Ordinal)) continue;
            var data = trimmed.Substring("FINGERPRINT=".Length);
            var parts = data.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var ints = new int[parts.Length];
            int n = 0;
            foreach (var part in parts)
                if (int.TryParse(part, out var v)) ints[n++] = v;
            if (n == 0) return null;
            return AudioFingerprint.Pack(ints, n);
        }
        return null;
    }

    private static string? ResolveFpcalc(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "fpcalc.exe"),
            Path.Combine(baseDir, "tools", "fpcalc.exe"),
            Path.Combine(baseDir, "tools", "chromaprint", "fpcalc.exe"),
            Path.Combine(baseDir, "fpcalc"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Попробовать PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "fpcalc.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }
}

/// <summary>Упаковка/распаковка аудио-отпечатка (массива int32) и построение множества субсигнатур.</summary>
public static class AudioFingerprint
{
    public static byte[] Pack(int[] ints, int count)
    {
        var bytes = new byte[count * 4];
        Buffer.BlockCopy(ints, 0, bytes, 0, count * 4);
        return bytes;
    }

    public static int[] Unpack(byte[] packed)
    {
        var ints = new int[packed.Length / 4];
        Buffer.BlockCopy(packed, 0, ints, 0, ints.Length * 4);
        return ints;
    }

    /// <summary>Множество субсигнатур (uint) для оценки схожести по Жаккару.</summary>
    public static HashSet<uint> ToSet(byte[] packed)
    {
        var ints = Unpack(packed);
        var set = new HashSet<uint>(ints.Length);
        foreach (var i in ints) set.Add((uint)i);
        return set;
    }
}
