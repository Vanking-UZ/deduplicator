using Deduplicator.Core.Engine;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Tests;

public sealed class PhysicalDeviceParallelismTests
{
    [Fact]
    public void Read_planner_preserves_runtime_physical_key_and_hdd_is_sequential()
    {
        var volume = new VolumeRecord
        {
            Id = 7,
            Label = "Архив",
            HasSeekPenalty = true,
            PhysicalDiskNumber = 3,
            PhysicalDeviceKey = "disk:3"
        };

        var plan = VolumeReadPlanner.ForVolume(volume, volume.Id, requested: 16);

        Assert.Equal("disk:3", plan.PhysicalDeviceKey);
        Assert.True(plan.PreferSequential);
        Assert.Equal(1, plan.ContentDop);
    }

    [Fact]
    public void Same_hdd_partitions_form_one_sequential_device_plan()
    {
        var plans = VolumeParallelExecutor.GroupPhysicalDevices(new[]
        {
            new VolumeExecutionProfile(10, "disk:0", 1, true),
            new VolumeExecutionProfile(11, "DISK:0", 1, true)
        });

        var device = Assert.Single(plans);
        Assert.Equal(new long[] { 10, 11 }, device.VolumeIds);
        Assert.True(device.PreferSequential);
        Assert.Equal(1, device.ContentDop);
    }

    [Fact]
    public void Same_ssd_partitions_share_one_bounded_device_dop()
    {
        var plans = VolumeParallelExecutor.GroupPhysicalDevices(new[]
        {
            new VolumeExecutionProfile(20, "disk:2", 8, false),
            new VolumeExecutionProfile(21, "disk:2", 6, false)
        });

        var device = Assert.Single(plans);
        Assert.False(device.PreferSequential);
        Assert.Equal(6, device.ContentDop); // не 8+6: очередь одного SSD не умножается разделами
    }

    [Fact]
    public void Independent_devices_keep_separate_plans_and_respect_global_quota()
    {
        var plans = VolumeParallelExecutor.GroupPhysicalDevices(new[]
        {
            new VolumeExecutionProfile(1, "disk:0", 1, true),
            new VolumeExecutionProfile(2, "disk:0", 1, true),
            new VolumeExecutionProfile(3, "disk:1", 8, false),
            new VolumeExecutionProfile(4, "disk:2", 8, false)
        });

        Assert.Equal(3, plans.Count);
        var quotas = VolumeParallelExecutor.DistributeCap(
            plans.Select(p => (p.PhysicalDeviceKey, p.ContentDop)).ToList(), cap: 5);

        Assert.Equal(3, quotas.Count);
        Assert.Equal(1, quotas["disk:0"]);
        Assert.All(quotas.Values, q => Assert.True(q >= 1));
        Assert.True(quotas.Values.Sum() <= 5);
    }

    [Fact]
    public void Executor_never_overlaps_partitions_of_one_sequential_device()
    {
        var items = Enumerable.Range(0, 40)
            .Select(i => (VolumeId: i % 2 == 0 ? 100L : 101L, Value: i))
            .ToArray();
        int active = 0;
        int maxActive = 0;

        VolumeParallelExecutor.RunByVolume(
            items,
            item => item.VolumeId,
            _ => 1,
            _ => "disk:9",
            _ => true,
            maxPhysicalDeviceParallelism: 4,
            CancellationToken.None,
            _ =>
            {
                int current = Interlocked.Increment(ref active);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maxActive);
                    if (seen >= current) break;
                } while (Interlocked.CompareExchange(ref maxActive, current, seen) != seen);
                Thread.SpinWait(20_000);
                Interlocked.Decrement(ref active);
            });

        Assert.Equal(1, maxActive);
    }
}
