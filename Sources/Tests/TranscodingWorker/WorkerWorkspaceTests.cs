using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证缓存空间仅计算实际占用和未完成的预留。</summary>
public sealed class WorkerWorkspaceTests
{
    private const long MIB = 1024 * 1024;

    /// <summary>部分下载恢复只保留尚未接收的原片空间。</summary>
    [Fact]
    public void PartialDownloadReservesOnlyMissingInputBytes()
    {
        WithDirectory(directory =>
        {
            using var workspace = new WorkerWorkspace("test-worker");
            var assignment = Assignment("first", "source-a");
            var configuration = Configuration(directory, 100 * MIB);
            using (var lease = workspace.Acquire(configuration, assignment))
            {
                WriteSize(lease.SourcePath + ".partial", 8 * MIB);
            }
            configuration.MaxCacheBytes = 74 * MIB;
            using var resumed = workspace.Acquire(configuration, assignment);
            Assert.Equal(8 * MIB, new FileInfo(resumed.SourcePath + ".partial").Length);
        });
    }

    /// <summary>并发任务写入的字节不应再次计入剩余预算。</summary>
    [Fact]
    public void WrittenInputAndOutputAreNotDoubleCountedForAnotherJob()
    {
        WithDirectory(directory =>
        {
            using var workspace = new WorkerWorkspace("test-worker");
            var configuration = Configuration(directory, 148 * MIB);
            using var first = workspace.Acquire(configuration, Assignment("first", "source-a"));
            WriteSize(first.SourcePath, 10 * MIB);
            WriteSize(Path.Combine(first.OutputDirectory, "chunk_00000.m4s"), 20 * MIB);
            using var second = workspace.Acquire(configuration, Assignment("second", "source-b"));
            Assert.NotEqual(first.SourcePath, second.SourcePath);
            Assert.True(File.Exists(Path.Combine(first.OutputDirectory, "chunk_00000.m4s")));
        });
    }

    /// <summary>多档位共享下载只保留一份输入预算，释放单档不得失去保护。</summary>
    [Fact]
    public void ProfilesShareInputReservationUntilLastLeaseIsReleased()
    {
        WithDirectory(directory =>
        {
            using var workspace = new WorkerWorkspace("test-worker");
            var configuration = Configuration(directory, 138 * MIB);
            var first = workspace.Acquire(configuration, Assignment("first", "source-a"));
            using var second = workspace.Acquire(configuration, Assignment("second", "source-a"));
            Assert.Equal(first.SourcePath, second.SourcePath);
            first.Dispose();
            configuration.MaxCacheBytes = 74 * MIB;
            Assert.Throws<WorkerCapacityUnavailableException>(() => workspace.Acquire(configuration, Assignment("third", "source-b")));
        });
    }

    /// <summary>热更新临时目录后，缓存额度按目录计算，旧任务继续受磁盘预留保护。</summary>
    [Fact]
    public void ChangingTemporaryRootKeepsIndependentCacheBudgets()
    {
        WithDirectory(directory =>
        {
            using var workspace = new WorkerWorkspace("test-worker");
            var configuration = Configuration(Path.Combine(directory, "old"), 74 * MIB);
            using var first = workspace.Acquire(configuration, Assignment("first", "source-a"));
            configuration.TempDirectory = Path.Combine(directory, "new");
            using var second = workspace.Acquire(configuration, Assignment("second", "source-b"));
            Assert.NotEqual(Path.GetDirectoryName(first.SourcePath), Path.GetDirectoryName(second.SourcePath));
        });
    }

    /// <summary>同一物理节点目录只允许一个进程写入，退出后所有权可以重新取得。</summary>
    [Fact]
    public void RootOwnershipIsExclusiveAndReleasedOnExit()
    {
        WithDirectory(directory =>
        {
            var configuration = Configuration(directory, 100 * MIB);
            using var second = new WorkerWorkspace("test-worker");
            using (var first = new WorkerWorkspace("test-worker"))
            using (first.Acquire(configuration, Assignment("first", "source-a")))
            {
                Assert.Throws<IOException>(() => second.Acquire(configuration, Assignment("second", "source-b")));
            }
            using var resumed = second.Acquire(configuration, Assignment("second", "source-b"));
            Assert.True(Directory.Exists(resumed.JobDirectory));
        });
    }

    /// <summary>容量紧张时不得回收尚在等待认领的输出及共享输入，解除保护后才能按预算回收。</summary>
    [Fact]
    public void PendingRecoveryPinsSurviveCapacityPressureAndRootChanges()
    {
        WithDirectory(directory =>
        {
            var configuration = Configuration(directory, 256 * MIB);
            var firstAssignment = Assignment("first", "source-a");
            var secondAssignment = Assignment("second", "source-b");
            WorkerWorkspaceLease first;
            WorkerWorkspaceLease second;
            using (var creating = new WorkerWorkspace("test-worker"))
            {
                first = creating.Acquire(configuration, firstAssignment);
                second = creating.Acquire(configuration, secondAssignment);
                WriteSize(first.SourcePath, 10 * MIB);
                WriteSize(second.SourcePath, 10 * MIB);
                WriteSize(Path.Combine(first.OutputDirectory, "encoded.mp4"), MIB);
                WriteSize(Path.Combine(second.OutputDirectory, "encoded.mp4"), MIB);
                first.Dispose();
                second.Dispose();
            }
            using var workspace = new WorkerWorkspace("test-worker");
            using var firstPin = workspace.ProtectCheckpointDirectory(first.JobDirectory, firstAssignment);
            using var secondPin = workspace.ProtectCheckpointDirectory(second.JobDirectory, secondAssignment);
            configuration = Configuration(Path.Combine(directory, "new-root"), 80 * MIB);
            using var otherRoot = workspace.Acquire(configuration, Assignment("new-root", "source-c"));
            Assert.Throws<WorkerCapacityUnavailableException>(() => workspace.Acquire(configuration, firstAssignment, checkpointDirectory: first.JobDirectory));
            Assert.True(File.Exists(first.SourcePath));
            Assert.True(File.Exists(second.SourcePath));
            Assert.True(File.Exists(Path.Combine(second.OutputDirectory, "encoded.mp4")));
            secondPin.Dispose();
            using var resumed = workspace.Acquire(configuration, firstAssignment, checkpointDirectory: first.JobDirectory);
            Assert.True(File.Exists(first.SourcePath));
            Assert.False(File.Exists(second.SourcePath));
        });
    }

    /// <summary>同源两个待恢复任务中解除其中一个保护，不能释放另一个仍需使用的共享输入。</summary>
    [Fact]
    public void SharedPendingInputRemainsPinnedUntilLastRecoveryReleasesIt()
    {
        WithDirectory(directory =>
        {
            var configuration = Configuration(directory, 256 * MIB);
            var firstAssignment = Assignment("first", "shared");
            var secondAssignment = Assignment("second", "shared");
            WorkerWorkspaceLease first;
            WorkerWorkspaceLease second;
            using (var creating = new WorkerWorkspace("test-worker"))
            {
                first = creating.Acquire(configuration, firstAssignment);
                second = creating.Acquire(configuration, secondAssignment);
                WriteSize(first.SourcePath, 10 * MIB);
                WriteSize(Path.Combine(first.OutputDirectory, "encoded.mp4"), MIB);
                WriteSize(Path.Combine(second.OutputDirectory, "encoded.mp4"), MIB);
                first.Dispose();
                second.Dispose();
            }
            using var workspace = new WorkerWorkspace("test-worker");
            using var firstPin = workspace.ProtectCheckpointDirectory(first.JobDirectory, firstAssignment);
            using var secondPin = workspace.ProtectCheckpointDirectory(second.JobDirectory, secondAssignment);
            firstPin.Dispose();
            configuration.MaxCacheBytes = 80 * MIB;
            Assert.Throws<WorkerCapacityUnavailableException>(() => workspace.Acquire(configuration, Assignment("third", "source-c")));
            Assert.True(File.Exists(second.SourcePath));
            Assert.True(File.Exists(Path.Combine(second.OutputDirectory, "encoded.mp4")));
        });
    }

    private static WorkerConfiguration Configuration(string directory, long capacity) => new()
    {
        TempDirectory = directory,
        MaxCacheBytes = capacity,
        MinimumFreeBytes = 0
    };

    private static TranscodeAssignment Assignment(string jobId, string assetId) => new()
    {
        JobId = jobId,
        AssetId = assetId,
        Attempt = 1,
        SourceVersion = "1",
        SourceLength = 10 * MIB,
        SourceETag = "\"test-source\""
    };

    private static void WriteSize(string path, long length)
    {
        using var stream = File.Create(path);
        stream.SetLength(length);
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-workspace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
