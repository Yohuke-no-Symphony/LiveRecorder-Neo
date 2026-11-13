using Xunit;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证统一实例目录的容量统计边界。</summary>
public sealed class StorageLayoutTests
{
    /// <summary>展示实例根目录，并分别统计共享缓存和录像回收站。</summary>
    [Fact]
    public async Task VolumeUsesInstanceRootAndSeparatesCacheFromRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-storage-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "data"), Path.Combine(directory, "records"));
            paths.EnsureDirectories();
            var artwork = Path.Combine(paths.CacheDirectory, "artwork");
            var playback = Path.Combine(paths.CacheDirectory, "playback");
            var trash = Path.Combine(paths.RecordsDirectory, ".trash");
            Directory.CreateDirectory(artwork);
            Directory.CreateDirectory(playback);
            Directory.CreateDirectory(trash);
            await File.WriteAllBytesAsync(Path.Combine(artwork, "avatar.image"), new byte[7]);
            await File.WriteAllBytesAsync(Path.Combine(playback, "window.ts"), new byte[11]);
            await File.WriteAllBytesAsync(Path.Combine(trash, "clip.mp4"), new byte[13]);
            await File.WriteAllBytesAsync(Path.Combine(paths.RecordsDirectory, "recording.flv"), new byte[17]);
            await File.WriteAllBytesAsync(Path.Combine(paths.KeysDirectory, "key.xml"), new byte[19]);
            var bootstrap = new BootstrapStore(paths, new JobTestSecretProtector());
            var storage = new StorageService(new MongoContext(bootstrap), new JobTestSettingsStore(), paths);

            var volume = await storage.GetVolumeAsync();

            Assert.Equal(paths.RecordsDirectory, volume.Path);
            Assert.Equal(0L, volume.CacheBytes);
            Assert.Equal(13L, volume.TrashBytes);
            var volumes = await storage.GetVolumesAsync();
            Assert.Equal(2, volumes.Count);
            Assert.Equal(paths.RecordsDirectory, volumes[0].Path);
            Assert.Equal("recordings", volumes[0].Id);
            Assert.Equal(2, volumes.Select(value => value.Id).Distinct().Count());
            var dataVolume = Assert.Single(volumes, value => value.Path == paths.DataDirectory);
            var recordsVolume = Assert.Single(volumes, value => value.Path == paths.RecordsDirectory);
            Assert.Equal(18L, dataVolume.CacheBytes);
            Assert.Equal(0L, dataVolume.TrashBytes);
            Assert.Equal(0L, recordsVolume.CacheBytes);
            Assert.Equal(13L, recordsVolume.TrashBytes);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
