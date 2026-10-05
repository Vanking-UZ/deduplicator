namespace Deduplicator.Core.Services;

/// <summary>Расположение файлов приложения (БД, карантин, кэш миниатюр, настройки, лог).</summary>
public sealed class AppPaths
{
    public const string PortableMarkerFileName = "Deduplicator.portable";

    public string RootDir { get; }
    public string DbPath { get; }
    public string QuarantineDir { get; }
    public string ThumbnailDir { get; }
    public string SettingsPath { get; }
    public string FeedbackDir { get; }
    public string FeedbackDraftPath { get; }
    public string LogDir { get; }
    public string LogPath { get; }

    public AppPaths(string? rootDir = null)
    {
        RootDir = ResolveRoot(rootDir);

        DbPath = Path.Combine(RootDir, "deduplicator.db");
        QuarantineDir = Path.Combine(RootDir, "Карантин");
        ThumbnailDir = Path.Combine(RootDir, "cache", "thumbnails");
        SettingsPath = Path.Combine(RootDir, "settings.json");
        FeedbackDir = Path.Combine(RootDir, "feedback");
        FeedbackDraftPath = Path.Combine(FeedbackDir, "ui-feedback-draft.json");
        // Лог в подпапке logs\ (по ТЗ MVP): чтобы пользователь/тестировщик легко его находил.
        LogDir = Path.Combine(RootDir, "logs");
        LogPath = Path.Combine(LogDir, "deduplicator.log");

        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(QuarantineDir);
        Directory.CreateDirectory(ThumbnailDir);
        Directory.CreateDirectory(FeedbackDir);
        Directory.CreateDirectory(LogDir);
    }

    private static string ResolveRoot(string? rootDir)
    {
        if (!string.IsNullOrWhiteSpace(rootDir))
            return Path.GetFullPath(rootDir);

        // Автотесты и диагностические сборки могут изолировать данные процесса, не затрагивая
        // настоящий профиль пользователя. Переменная не включается приложением самостоятельно.
        string? overridden = Environment.GetEnvironmentVariable("DEDUP_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden))
            return Path.GetFullPath(overridden);

        // Portable-сборка становится действительно переносимой только по явному маркеру рядом
        // с EXE. Обычный установщик маркер не кладёт и продолжает хранить данные в профиле Windows.
        string portableMarker = Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName);
        if (File.Exists(portableMarker))
            return Path.Combine(AppContext.BaseDirectory, "Data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deduplicator");
    }
}
