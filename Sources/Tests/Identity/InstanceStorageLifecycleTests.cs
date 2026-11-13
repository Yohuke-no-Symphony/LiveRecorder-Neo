using System.Security.Cryptography;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证实例检查和密钥读取不会在提交安装前写入目录或令牌。</summary>
public sealed class InstanceStorageLifecycleTests
{
    [Fact]
    public async Task InitialAndCandidateInspectionAreReadOnlyUntilTheInstallationCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-instance-lifecycle-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new InstanceStorageFactory();
            var initial = factory.CreateInitial(Path.Combine(directory, "initial-data"), Path.Combine(directory, "initial-records"), false);
            Assert.False(string.IsNullOrWhiteSpace(initial.Bootstrap.InstallationToken));
            var protectedValue = initial.SecretProtector.Protect("http-only-secret");
            Assert.Equal("http-only-secret", initial.SecretProtector.Unprotect(protectedValue));
            Assert.False(Directory.Exists(directory));
            var candidate = factory.CreateCandidate(Path.Combine(directory, "selected-data"), Path.Combine(directory, "selected-records"));
            Assert.False(Directory.Exists(directory));

            Assert.Throws<CryptographicException>(() => candidate.SecretProtector.Protect("must-not-generate-a-key"));

            Assert.False(Directory.Exists(directory));
            var committed = factory.CreateForInstallationCommit(candidate);
            Assert.True(Directory.Exists(committed.Paths.DataDirectory));
            Assert.True(Directory.Exists(committed.Paths.RecordsDirectory));
            Assert.True(Directory.Exists(committed.Paths.KeysDirectory));
            Assert.False(File.Exists(committed.Paths.BootstrapFile));
            Assert.False(File.Exists(committed.Paths.InstallationTokenFile));
            await committed.Bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true });
            Assert.True(File.Exists(committed.Paths.BootstrapFile));
            Assert.True(File.Exists(committed.Paths.InstallationTokenFile));
            Assert.NotEmpty(Directory.EnumerateFiles(committed.Paths.KeysDirectory, "key-*.xml"));
            Assert.False(Directory.Exists(initial.Paths.DataDirectory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task MissingMainConfigurationDoesNotLoadOrRewriteAnOldBootstrapOrToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-instance-lifecycle-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(directory, "Data");
            var records = Path.Combine(directory, "records");
            Directory.CreateDirectory(data);
            var bootstrapFile = Path.Combine(data, "bootstrap.json");
            var tokenFile = Path.Combine(data, "installation-token");
            await File.WriteAllTextAsync(bootstrapFile, "old-protected-bootstrap-without-its-key");
            await File.WriteAllTextAsync(tokenFile, "old-installation-token");

            var initial = new InstanceStorageFactory().CreateInitial(data, records, false);

            Assert.False(initial.IsInstalled);
            Assert.Null(initial.Bootstrap.Current);
            Assert.NotEqual("old-installation-token", initial.Bootstrap.InstallationToken);
            Assert.Equal("old-protected-bootstrap-without-its-key", await File.ReadAllTextAsync(bootstrapFile));
            Assert.Equal("old-installation-token", await File.ReadAllTextAsync(tokenFile));
            Assert.False(Directory.Exists(records));
            Assert.False(Directory.Exists(Path.Combine(data, "keys")));
            Assert.False(Directory.Exists(Path.Combine(data, "cache")));
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
