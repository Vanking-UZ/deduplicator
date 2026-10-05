using System.Globalization;
using System.Text;

namespace Deduplicator.Core.Util;

/// <summary>
/// Минимальный потокобезопасный файловый лог (NFR-1: ошибки «пропускаем с логированием»).
/// Путь задаётся один раз при старте; до этого вызовы — no-op. Сам логгер никогда не бросает.
/// </summary>
public static class AppLog
{
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const int ArchivedLogCount = 5;
    private static string? _path;
    private static string? _runMarkerPath;
    private static readonly object _gate = new();
    private static readonly string _runId = Guid.NewGuid().ToString("N")[..12];

    // Логи содержат пути с кириллицей (имя пользователя). Без BOM Notepad/PowerShell читают файл
    // в системной ANSI-кодировке -> кракозябры. Пишем BOM при первом создании файла, дальше — без BOM.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string RunId => _runId;

    public static void Configure(string path)
    {
        bool previousRunCrashed;
        lock (_gate)
        {
            _path = path;
            _runMarkerPath = path + ".running";
            try
            {
                previousRunCrashed = File.Exists(_runMarkerPath);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_runMarkerPath, _runId, Utf8NoBom);
            }
            catch { previousRunCrashed = false; }
        }
        Write("INFO Application run started");
        if (previousRunCrashed)
            Write("WARN Previous application run did not finish cleanly; recovery checks will run");
    }

    public static void MarkCleanShutdown()
    {
        Write("INFO Application run finished cleanly");
        lock (_gate)
        {
            try
            {
                if (!string.IsNullOrEmpty(_runMarkerPath) && File.Exists(_runMarkerPath))
                    File.Delete(_runMarkerPath);
            }
            catch { /* marker is diagnostic only */ }
        }
    }

    public static void Write(string message)
    {
        var path = _path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:o} run={_runId} {Flatten(message)}{Environment.NewLine}");
            lock (_gate)
            {
                // Каталог логов мог быть удалён после старта — восстанавливаем перед записью.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateIfNeeded(path, Utf8NoBom.GetByteCount(line));
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty, Utf8Bom);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch { /* лог — вспомогательный, не мешаем работе */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex}");

    private static string Flatten(string value) => value
        .Replace("\r\n", " ⏎ ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    private static void RotateIfNeeded(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaxLogBytes) return;

        // deduplicator.log.1 — самый свежий архив. Все операции best-effort и выполняются
        // под тем же lock, поэтому параллельные писатели не могут потерять строку между ротациями.
        var oldest = path + "." + ArchivedLogCount;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = ArchivedLogCount - 1; i >= 1; i--)
        {
            var source = path + "." + i;
            if (File.Exists(source)) File.Move(source, path + "." + (i + 1), overwrite: true);
        }
        File.Move(path, path + ".1", overwrite: true);
    }
}
