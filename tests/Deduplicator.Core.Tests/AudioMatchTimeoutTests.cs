using System.Diagnostics;
using Deduplicator.Core.Audio;

namespace Deduplicator.Core.Tests;

public sealed class AudioMatchTimeoutTests
{
    [Fact]
    public void Fingerprint_process_is_stopped_after_per_file_timeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        string executable = PowerShellPath();
        string pidFile = Path.Combine(Path.GetTempPath(), $"dedup-fpcalc-{Guid.NewGuid():N}.pid");
        try
        {
            var service = new AudioMatchService(executable, TimeSpan.FromSeconds(2),
                (_, _) => PowerShellStartInfo(
                    $"Set-Content -LiteralPath '{EscapeSingleQuoted(pidFile)}' -Value $PID; Start-Sleep -Seconds 30"));

            var watch = Stopwatch.StartNew();
            var result = service.ComputeFingerprint("ignored.mp3");
            watch.Stop();

            Assert.Null(result);
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
            int childPid = int.Parse(File.ReadAllText(pidFile).Trim());
            Assert.True(ProcessHasExited(childPid), "fpcalc process must not survive its timeout");
        }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }

    [Fact]
    public void Caller_cancellation_stops_process_and_is_propagated()
    {
        if (!OperatingSystem.IsWindows()) return;

        string executable = PowerShellPath();
        string pidFile = Path.Combine(Path.GetTempPath(), $"dedup-fpcalc-{Guid.NewGuid():N}.pid");
        try
        {
            var service = new AudioMatchService(executable, TimeSpan.FromSeconds(30),
                (_, _) => PowerShellStartInfo(
                    $"Set-Content -LiteralPath '{EscapeSingleQuoted(pidFile)}' -Value $PID; Start-Sleep -Seconds 30"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            Assert.ThrowsAny<OperationCanceledException>(
                () => service.ComputeFingerprint("ignored.mp3", cancellation.Token));

            int childPid = int.Parse(File.ReadAllText(pidFile).Trim());
            Assert.True(ProcessHasExited(childPid), "cancelled fpcalc process must not remain in the background");
        }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }

    [Fact]
    public void Successful_process_output_is_still_parsed()
    {
        if (!OperatingSystem.IsWindows()) return;

        string executable = PowerShellPath();
        var service = new AudioMatchService(executable, TimeSpan.FromSeconds(5),
            (_, _) => PowerShellStartInfo("Write-Output 'FINGERPRINT=1,2,-3'"));

        var packed = Assert.IsType<byte[]>(service.ComputeFingerprint("ignored.mp3"));

        Assert.Equal(new[] { 1, 2, -3 }, AudioFingerprint.Unpack(packed));
    }

    private static ProcessStartInfo PowerShellStartInfo(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return info;
    }

    private static string PowerShellPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.WaitForExit(2_000)) return true;
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
