using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deduplicator.Core.Services;

public sealed class UpdateInfo
{
    public string LatestVersion { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Notes { get; init; }
    public bool IsNewer { get; init; }
}

public interface IUpdateService
{
    string CurrentVersion { get; }

    /// <summary>
    /// Проверить наличие новой версии на GitHub Releases. Сетевой запрос выполняется
    /// только по явному вызову (офлайн-приложение, NFR-10). repo в формате "owner/name".
    /// </summary>
    Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default);
}

/// <summary>Проверка обновлений через публичный GitHub Releases API (без зависимостей).</summary>
public sealed class UpdateService : IUpdateService
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly Regex RepositoryPattern = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Один переиспользуемый HttpClient (рекомендация .NET — не создавать на каждый запрос).
    private static readonly HttpClient Http = CreateClient();
    private readonly HttpClient _http;
    private readonly string? _currentVersion;

    public UpdateService() : this(Http) { }

    public UpdateService(HttpClient httpClient, string? currentVersion = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _currentVersion = currentVersion;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Deduplicator-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public string CurrentVersion =>
        _currentVersion ?? (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        repo = repo.Trim();
        if (!RepositoryPattern.IsMatch(repo)) return null;

        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        if (resp.Content.Headers.ContentLength is > MaxResponseBytes) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var payload = await ReadLimitedAsync(stream, MaxResponseBytes, ct);
        if (payload is null) return null;
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string htmlUrl = root.TryGetProperty("html_url", out var h) ? SafeReleaseUrl(h.GetString()) : "";
        string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        if (notes?.Length > 4000) notes = notes[..4000] + "…";

        var latest = NormalizeVersion(tag);
        bool isNewer = Version.TryParse(latest, out var lv) &&
                       Version.TryParse(NormalizeVersion(CurrentVersion), out var cv) &&
                       lv > cv;

        return new UpdateInfo
        {
            LatestVersion = string.IsNullOrEmpty(tag) ? latest : tag,
            Url = htmlUrl,
            Notes = notes,
            IsNewer = isNewer
        };
    }

    private static string NormalizeVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        // Оставляем только числовую часть "1.2.3".
        var span = s.AsSpan();
        int end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.')) end++;
        var num = s[..end].Trim('.');
        return string.IsNullOrEmpty(num) ? "0.0" : num;
    }

    private static string SafeReleaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return uri.AbsoluteUri;
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) return null;
            output.Write(buffer, 0, read);
        }
    }
}
