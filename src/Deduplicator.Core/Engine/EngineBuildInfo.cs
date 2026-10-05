using System.Reflection;

namespace Deduplicator.Core.Engine;

/// <summary>
/// Версии движка/сборок для диагностики (About-экран и логи). Помогает поймать ситуацию
/// «исходники новые, а запущена старая сборка»: если в логе нет этих версий — это старый бинарник.
/// </summary>
public static class EngineBuildInfo
{
    public static int EngineVersion => IncrementalScanPlanner.EngineVersion;

    public static string CoreVersion => VersionOf(typeof(EngineBuildInfo).Assembly);

    public static string FastEngineVersion => VersionOf(typeof(FastDedup.Engine.FastFileLister).Assembly);

    public static string Summary =>
        $"ExactDuplicateEngineV2, IndexFormat v{EngineVersion}, Core {CoreVersion}, FastDedup.Engine {FastEngineVersion}, " +
        "Pipeline=size->partial(RandomAccess)->full->byte-verify";

    private static string VersionOf(Assembly a) =>
        a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? a.GetName().Version?.ToString()
        ?? "?";
}
