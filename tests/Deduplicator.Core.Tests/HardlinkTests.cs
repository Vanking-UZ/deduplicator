using System.Runtime.InteropServices;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

public class HardlinkTests : IDisposable
{
    private readonly string _dir;

    public HardlinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dedup_hl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private FileOpsService MakeOps()
    {
        var db = new Database(Path.Combine(_dir, "h.db"));
        db.Initialize();
        return new FileOpsService(new OperationLogRepository(db), new FileRepository(db),
            new GroupRepository(db), new HashService(), Path.Combine(_dir, "q"));
    }

    [Fact]
    public void Hardlink_replaces_duplicate_and_preserves_content()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var data = new byte[5000];
        new Random(5).NextBytes(data);
        var rep = Path.Combine(_dir, "rep.bin");
        var dup = Path.Combine(_dir, "dup.bin");
        File.WriteAllBytes(rep, data);
        File.WriteAllBytes(dup, (byte[])data.Clone());

        var ops = MakeOps();
        var rec = new FileRecord { Id = 0, Name = "dup.bin", Size = data.Length };
        bool ok = ops.ReplaceWithHardLink(rec, dup, rep, null);

        Assert.True(ok);
        Assert.True(File.Exists(dup));               // дубликат остался (как ссылка)
        Assert.True(File.Exists(rep));               // эталон цел
        Assert.Equal(data, File.ReadAllBytes(dup));  // содержимое сохранено
    }

    [Fact]
    public void Hardlink_refuses_and_keeps_both_when_content_differs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var rep = Path.Combine(_dir, "rep.bin");
        var dup = Path.Combine(_dir, "dup.bin");
        File.WriteAllBytes(rep, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(dup, new byte[] { 1, 2, 3, 9 }); // другое содержимое

        var ops = MakeOps();
        var rec = new FileRecord { Id = 0, Name = "dup.bin", Size = 4 };
        bool ok = ops.ReplaceWithHardLink(rec, dup, rep, null);

        Assert.False(ok);                            // отказ — это не дубликат
        Assert.True(File.Exists(dup));               // оба файла целы
        Assert.True(File.Exists(rep));
        Assert.Equal(new byte[] { 1, 2, 3, 9 }, File.ReadAllBytes(dup));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
