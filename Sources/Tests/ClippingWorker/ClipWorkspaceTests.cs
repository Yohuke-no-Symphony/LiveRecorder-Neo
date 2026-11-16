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

    /// <summary>Disposing a task removes partial downloads, rebuilt media and output while preserving other active tasks.</summary>
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

    /// <summary>A new process owner removes orphan task files left by an interrupted process.</summary>
    [Fact]
    public async Task StartupSweepsOrphanTaskDirectories()
    {
        using var fixture = new ClipTestFixture();
        var orphan = Path.Combine(fixture.Root, "live-recorder-clipping-worker", "worker", "jobs", "orphan-1");
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(Path.Combine(orphan, "old.range.partial"), [1]);
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration, ClipTestFixture.SmallAssignment());
        Assert.False(Directory.Exists(orphan));
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
}
