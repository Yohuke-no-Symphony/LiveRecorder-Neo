using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Checks that each Worker configuration API exposes and saves only applicable user settings.</summary>
public sealed class WorkerConfigurationHttpTests
{
    /// <summary>All management responses retain independent settings shapes, including legacy clients sending extra fields.</summary>
    [MongoHttpFact]
    public async Task EachKindExposesOnlyItsOwnConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var kinds = new[] { "Transcoding", "Clipping", "Recording", "AI" };
        foreach (var kind in kinds)
        {
            using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = kind, kind });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var worker = (await created.Content.ReadFromJsonAsync<JsonObject>())!["worker"]!.AsObject();
            AssertShape(kind, worker["configuration"]!.AsObject());
            var configuration = worker["configuration"]!.DeepClone().AsObject();
            configuration["uploadBytesPerSecond"] = 1048576;
            if (kind == "AI")
            {
                Assert.Equal(1, configuration["maxConcurrentJobs"]!.GetValue<int>());
                configuration["maxConcurrentJobs"] = 3;
                configuration["maxConcurrentEncodes"] = 19;
                configuration["ffmpegPath"] = "unused-old-tool";
                configuration["ffprobePath"] = "unused-old-tool";
            }
            if (kind != "Transcoding")
            {
                configuration["gpuConcurrency"] = 47;
                configuration["cpuFallbackMode"] = "Disabled";
            }
            if (kind == "Recording")
            {
                configuration["ffmpegPath"] = "unused-old-tool";
                configuration["downloadBytesPerSecond"] = 1048576;
                configuration["clipEncodingThreads"] = 19;
            }
            if (kind == "Clipping")
            {
                configuration["clipEncodingThreads"] = 3;
            }
            var id = worker["id"]!.GetValue<string>();
            using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + id,
                new { name = kind, enabled = true, configuration, expectedRevision = worker["configurationRevision"]!.GetValue<int>() });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var saved = (await updated.Content.ReadFromJsonAsync<JsonObject>())!;
            AssertShape(kind, saved["configuration"]!.AsObject());
            Assert.Equal(1048576, saved["configuration"]!["uploadBytesPerSecond"]!.GetValue<long>());
            if (kind == "AI")
            {
                Assert.Equal(3, saved["configuration"]!["maxConcurrentJobs"]!.GetValue<int>());
            }
            if (kind == "Clipping")
            {
                Assert.Equal(3, saved["configuration"]!["clipEncodingThreads"]!.GetValue<int>());
            }
        }
        var listed = (await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/transcoding/workers"))!;
        Assert.Equal(4, listed.Count);
        foreach (var row in listed)
        {
            AssertShape(row!["kind"]!.GetValue<string>(), row["configuration"]!.AsObject());
            Assert.False(row["isOnline"]!.GetValue<bool>());
        }
    }

    /// <summary>Only clipping validates its exact-encoding thread setting; unsupported parameters never leak to other kinds.</summary>
    [MongoHttpFact]
    public async Task ClippingThreadLimitsAreValidatedAtTheManagementBoundary()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "clip", kind = "Clipping" });
        var worker = (await created.Content.ReadFromJsonAsync<JsonObject>())!["worker"]!.AsObject();
        var configuration = worker["configuration"]!.DeepClone().AsObject();
        foreach (var value in new[] { 0, 129 })
        {
            configuration["clipEncodingThreads"] = value;
            using var invalid = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + worker["id"]!.GetValue<string>(),
                new { name = "clip", enabled = true, configuration, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    private static void AssertShape(string kind, JsonObject configuration)
    {
        string[] expected = kind switch
        {
            "Transcoding" => ["ffmpegPath", "ffprobePath", "tempDirectory", "maxConcurrentEncodes", "gpuConcurrency", "cpuConcurrency",
                "cpuFallbackMode", "gpuWaitSeconds", "cpuLoadThreshold", "cpuSampleWindowSeconds", "cpuThreadFraction", "preferredEncoder",
                "deviceId", "maxCacheBytes", "minimumFreeBytes", "downloadBytesPerSecond", "uploadBytesPerSecond"],
            "Clipping" => ["ffmpegPath", "ffprobePath", "tempDirectory", "maxConcurrentEncodes", "clipEncodingThreads",
                "maxCacheBytes", "minimumFreeBytes", "downloadBytesPerSecond", "uploadBytesPerSecond"],
            "Recording" => ["tempDirectory", "maxConcurrentEncodes", "maxCacheBytes", "minimumFreeBytes", "uploadBytesPerSecond"],
            "AI" => ["tempDirectory", "maxConcurrentJobs", "maxCacheBytes", "minimumFreeBytes", "downloadBytesPerSecond", "uploadBytesPerSecond"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), configuration.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }
}
