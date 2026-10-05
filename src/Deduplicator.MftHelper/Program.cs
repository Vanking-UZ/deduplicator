using FastDedup.Engine;

namespace Deduplicator.MftHelper;

internal static class Program
{
    public static int Main(string[] args) => ElevatedMftHelperHost.Run(args);
}
