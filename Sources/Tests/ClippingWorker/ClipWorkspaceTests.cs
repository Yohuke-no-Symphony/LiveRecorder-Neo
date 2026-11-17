using Xunit;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

/// <summary>Verifies per-attempt temporary storage, capacity reservations and crash recovery.</summary>
public sealed class ClipWorkspaceTests
{
    /// <summary>Capacity is based on the small assigned range rather than the full source length.</summary>
    [Fact]
    public void ReservesOnlyRangeAndOutputCapacity()
    {
        using var fixture = new ClipTestFixture();
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration with { MaxCacheBytes = 128L * 1024 * 1024 },
            ClipTestFixture.SmallAssignment(sourceLength: 1024L * 1024 * 1024 * 1024));
        Assert.True(Directory.Exists(files.DirectoryPath));
    }

    /// <summary>Confirmed completion removes task files while preserving other active tasks.</summary>
    [Fact]
    public async Task ReleasesAllTaskFilesWithoutDeletingOtherAttempt()
    {
        using var fixture = new ClipTestFixture();
        using var workspace = new ClipWorkspace("worker");
        var first = workspace.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment("first"));
        using var second = workspace.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment("second"));
        await File.WriteAllBytesAsync(Path.Combine(first.DirectoryPath, "input.partial"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(first.DirectoryPath, "input.flv"), [2]);
        await File.WriteAllBytesAsync(first.OutputPath, [3]);

        first.Complete();
        first.Dispose();

        Assert.False(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(second.DirectoryPath));
    }

    /// <summary>Concurrent attempts reserve future output space until their workspace is released.</summary>
    [Fact]
    public void CapacityReservationPreventsConcurrentOvercommit()
    {
        using var fixture = new ClipTestFixture();
        var configuration = fixture.Configuration with { MaxCacheBytes = 90L * 1024 * 1024 };
        using var workspace = new ClipWorkspace("worker");
        var first = workspace.Acquire(configuration, ClipTestFixture.SmallAssignment("first"));
        Assert.ThrowsAny<IOException>(() => workspace.Acquire(configuration, ClipTestFixture.SmallAssignment("second")));
        first.Dispose();
        using var second = workspace.Acquire(configuration, ClipTestFixture.SmallAssignment("second"));
        Assert.True(Directory.Exists(second.DirectoryPath));
    }

    /// <summary>Changing the temporary root gives new work an independent cache budget.</summary>
    [Fact]
    public void NewRootDoesNotConsumeOldRootCacheBudget()
    {
        using var fixture = new ClipTestFixture();
        using var workspace = new ClipWorkspace("worker");
        var configuration = fixture.Configuration with { MaxCacheBytes = 90L * 1024 * 1024 };
        using var first = workspace.Acquire(configuration, ClipTestFixture.SmallAssignment("first"));
        using var second = workspace.Acquire(configuration with { TempDirectory = Path.Combine(fixture.Root, "new-root") },
            ClipTestFixture.SmallAssignment("second"));
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(second.DirectoryPath));
    }

    /// <summary>A new owner preserves interrupted work until its execution identity is reconciled.</summary>
    [Fact]
    public async Task StartupPreservesInterruptedTaskDirectories()
    {
        using var fixture = new ClipTestFixture();
        var orphan = Path.Combine(fixture.Root, "live-recorder-clipping-worker", "worker", "jobs", "orphan-1");
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(Path.Combine(orphan, "old.range.partial"), [1]);
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment());
        Assert.True(Directory.Exists(orphan));
    }

    /// <summary>Two processes cannot sweep or share one worker's active temporary tree.</summary>
    [Fact]
    public void RootOwnershipPreventsConcurrentSweep()
    {
        using var fixture = new ClipTestFixture();
        using var first = new ClipWorkspace("worker");
        using var active = first.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment());
        using var second = new ClipWorkspace("worker");
        Assert.Throws<IOException>(() => second.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment("other")));
        Assert.True(Directory.Exists(active.DirectoryPath));
    }

    /// <summary>待认领目录不能被首个恢复任务的容量回收删除，拒绝后解除保护才可回收。</summary>
    [Fact]
    public void PendingRecoveryDirectoryIsPinnedUntilReconciliation()
    {
        using var fixture = new ClipTestFixture();
        var configuration = fixture.Configuration;
        var firstAssignment = ClipTestFixture.SmallAssignment("first");
        var secondAssignment = ClipTestFixture.SmallAssignment("second");
        ClipWorkspaceLease first;
        ClipWorkspaceLease second;
        using (var creating = new ClipWorkspace("worker"))
        {
            first = creating.Acquire(configuration, firstAssignment);
            second = creating.Acquire(configuration, secondAssignment);
            using (var output = File.Create(first.OutputPath))
            {
                output.SetLength(48L * 1024 * 1024);
            }
            using (var output = File.Create(second.OutputPath))
            {
                output.SetLength(48L * 1024 * 1024);
            }
            first.Dispose();
            second.Dispose();
        }
        using var workspace = new ClipWorkspace("worker");
        using var firstPin = workspace.ProtectCheckpointDirectory(first.DirectoryPath);
        using var secondPin = workspace.ProtectCheckpointDirectory(second.DirectoryPath);
        var limited = configuration with { TempDirectory = Path.Combine(fixture.Root, "new-root"), MaxCacheBytes = 80L * 1024 * 1024 };
        using var otherRoot = workspace.Acquire(limited, ClipTestFixture.SmallAssignment("new-root"));
        Assert.Throws<ClipCapacityException>(() => workspace.Acquire(limited, firstAssignment, first.DirectoryPath));
        Assert.True(File.Exists(first.OutputPath));
        Assert.True(File.Exists(second.OutputPath));
        secondPin.Dispose();
        using var resumed = workspace.Acquire(limited, firstAssignment, first.DirectoryPath);
        Assert.True(File.Exists(first.OutputPath));
        Assert.False(Directory.Exists(second.DirectoryPath));
    }
}
