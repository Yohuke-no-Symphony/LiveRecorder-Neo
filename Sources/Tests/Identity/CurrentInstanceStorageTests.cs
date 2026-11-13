using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证实例原子发布后，可缓存的保护器只读取已发布目录的密钥。</summary>
public sealed class CurrentInstanceStorageTests
{
    [Fact]
    public async Task CachedProtectorsFollowThePublishedInstanceWithoutAcceptingThePreviousKeyRing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-instance-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new InstanceStorageFactory();
            var initial = factory.CreateInitial(Path.Combine(directory, "initial-data"), Path.Combine(directory, "initial-records"), false);
            var current = new CurrentInstanceStorage(initial);
            var paths = new CurrentAppPaths(current);
            var provider = new CurrentDataProtectionProvider(current);
            var cached = provider.CreateProtector("session").CreateProtector("cookie");
            var previousCipher = cached.Protect("previous-cookie");
            var cachedSecret = new CurrentSecretProtector(current);
            var previousSecret = cachedSecret.Protect("previous-secret");
            Assert.False(Directory.Exists(initial.Paths.DataDirectory));
            var candidate = factory.CreateForInstallationCommit(factory.CreateCandidate(Path.Combine(directory, "candidate-data"), Path.Combine(directory, "candidate-records")));
            var candidateCookie = candidate.DataProtectionProvider.CreateProtector("session").CreateProtector("cookie").Protect("candidate-cookie");
            await candidate.Bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true, DatabaseName = "candidate_database" });

            current.PublishInstalled(initial, candidate);

            Assert.True(current.IsInstalled);
            Assert.Equal(candidate.Paths.DataDirectory, paths.DataDirectory);
            Assert.Equal(candidate.Paths.RecordsDirectory, paths.RecordsDirectory);
            Assert.Equal("candidate-cookie", cached.Unprotect(candidateCookie));
            Assert.Throws<CryptographicException>(() => cached.Unprotect(previousCipher));
            Assert.Throws<CryptographicException>(() => cachedSecret.Unprotect(previousSecret));
            Assert.Throws<CryptographicException>(() => provider.CreateProtector("session").CreateProtector("other-purpose").Unprotect(candidateCookie));
            Assert.Equal("new-secret", candidate.SecretProtector.Unprotect(cachedSecret.Protect("new-secret")));
            Assert.NotEmpty(Directory.EnumerateFiles(candidate.Paths.KeysDirectory, "key-*.xml"));
            Assert.False(Directory.Exists(initial.Paths.DataDirectory));
            Assert.Equal("candidate_database", current.Current.Mongo.Database.DatabaseNamespace.DatabaseName);
            Assert.Throws<InvalidOperationException>(() => current.PublishInstalled(initial, candidate));
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
    public void UnverifiedCandidateCannotReplaceTheActiveInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-instance-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new InstanceStorageFactory();
            var initial = factory.CreateInitial(Path.Combine(directory, "initial-data"), Path.Combine(directory, "initial-records"), false);
            var current = new CurrentInstanceStorage(initial);
            var candidate = factory.CreateCandidate(Path.Combine(directory, "candidate-data"), Path.Combine(directory, "candidate-records"));
            var secret = initial.SecretProtector.Protect("still-active");

            Assert.Throws<InvalidOperationException>(() => current.PublishInstalled(initial, candidate));

            Assert.Same(initial, current.Current);
            Assert.False(current.IsInstalled);
            Assert.Equal("still-active", new CurrentSecretProtector(current).Unprotect(secret));
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
