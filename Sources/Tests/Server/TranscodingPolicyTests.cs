using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证物理引用、清晰度配方与派生文件发布边界。</summary>
public sealed class TranscodingPolicyTests
{
    /// <summary>新增转码引用不改变旧修复完成清单的输入指纹，升级后仍可恢复已发布作业。</summary>
    [Fact]
    public void TranscodeMetadataPreservesLegacyMediaJobFingerprint()
    {
        var segment = new RecordingSegment
        {
            Id = "segment", SessionId = "session", AttemptId = "record", RelativePath = "source.flv",
            DurationMs = 1000, CommittedBytes = 100, IsClosed = true, IndexRelativePath = "source.index.jsonl",
            CreatedAtUtc = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc)
        };
        var job = new MediaJob { Id = "job", Kind = "Repair", Title = "repair", EndMs = 1000, SourceSegments = [segment] };
        const string LEGACY_FINGERPRINT = "E56DC7FA48A5E5E0106220EB4F943C8425DFFDA560677F44A469A5C99AD2D4BA";
        Assert.Equal(LEGACY_FINGERPRINT, JobOutputManifest.Create(job).InputFingerprint);
        segment.AssetId = "physical";
        segment.PendingTranscodeProfileIds = ["720p"];
        Assert.Equal(LEGACY_FINGERPRINT, JobOutputManifest.Create(job).InputFingerprint);
    }

    /// <summary>归组改变逻辑身份和时间轴后仍然引用原物理资产。</summary>
    [Fact]
    public void ProjectionPreservesPhysicalIdentityAcrossSessionAndOffsetChanges()
    {
        var original = new BroadcastSession { DirectoryPath = "room/source" };
        var source = new RecordingSegment { AssetId = "physical", RelativePath = "recording.flv", StartOffsetMs = 123 };
        var projection = new BroadcastSession { DirectoryPath = "room/grouped" };
        var projected = new RecordingSegment { AssetId = source.AssetId, RootRelativePath = "room/source/recording.flv", StartOffsetMs = 10000, Revision = 7 };
        Assert.Equal(TranscodeAssetRegistry.AssetId(original, source), TranscodeAssetRegistry.AssetId(projection, projected));
        projected.AssetId = "repaired-physical";
        Assert.NotEqual(TranscodeAssetRegistry.AssetId(original, source), TranscodeAssetRegistry.AssetId(projection, projected));
    }

    /// <summary>尚未补写资产字段的旧档案也按原文件根引用去重。</summary>
    [Fact]
    public void LegacyProjectionUsesTheSameCanonicalRootReference()
    {
        var original = new BroadcastSession { DirectoryPath = Path.Combine("room", "source") };
        var source = new RecordingSegment { RelativePath = "recording.flv" };
        var projection = new BroadcastSession { DirectoryPath = Path.Combine("room", "grouped") };
        var projected = new RecordingSegment { RootRelativePath = Path.Combine(original.DirectoryPath, source.RelativePath) };
        Assert.Equal(TranscodeAssetRegistry.AssetId(original, source), TranscodeAssetRegistry.AssetId(projection, projected));
    }

    /// <summary>显示名和档位开关不会改变编码内容身份，实际编码参数会改变。</summary>
    [Fact]
    public void RecipeIgnoresPresentationButTracksEncodingParameters()
    {
        var profile = new TranscodeProfile();
        var expected = TranscodeRecipe.Hash(profile);
        Assert.Equal(expected, TranscodeRecipe.Hash(profile with { Id = "renamed", Name = "清晰", Enabled = false }));
        Assert.NotEqual(expected, TranscodeRecipe.Hash(profile with { Height = 1080 }));
        Assert.NotEqual(expected, TranscodeRecipe.Hash(profile with { VideoBitrateKbps = 1500 }));
        Assert.NotEqual(expected, TranscodeRecipe.Hash(profile with { MaxFrameRate = 30 }));
    }

    /// <summary>默认档位必须存在且启用，顺序必须精确覆盖所有档位。</summary>
    [Fact]
    public void SettingsRejectMissingDuplicateAndDisabledDefaults()
    {
        TranscodingSettingsPolicy.Validate(new());
        Assert.Throws<ArgumentException>(() => TranscodingSettingsPolicy.Validate(new() { QualityOrder = ["original", "original"] }));
        Assert.Throws<ArgumentException>(() => TranscodingSettingsPolicy.Validate(new() { DefaultPlaybackQualityId = "missing" }));
        Assert.Throws<ArgumentException>(() => TranscodingSettingsPolicy.Validate(new() { Profiles = [new() { Enabled = false }] }));
        TranscodingSettingsPolicy.Validate(new() { Profiles = [], QualityOrder = ["original"], DefaultPlaybackQualityId = "original" });
    }

    /// <summary>成品清单不能请求外部文件、越界路径、密钥或未声明的分片。</summary>
    [Theory]
    [InlineData("https://example.invalid/chunk_00000.m4s")]
    [InlineData("../chunk_00000.m4s")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://example.invalid/key\"\nchunk_00000.m4s")]
    [InlineData("chunk_00001.m4s")]
    public async Task PlaylistRejectsExternalAndUnlistedInputs(string uri)
    {
        using var fixture = new StorageUsageTestContext();
        var directory = fixture.Paths.RecordsDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "index.m3u8"), Playlist(uri));
        await Assert.ThrowsAsync<InfrastructureException>(() => TranscodeArtifactPolicy.ValidatePlaylistAsync(directory, Files(), CancellationToken.None));
    }

    /// <summary>独立完整 fMP4 清单通过白名单校验。</summary>
    [Fact]
    public async Task PlaylistAcceptsCompleteLocalVod()
    {
        using var fixture = new StorageUsageTestContext();
        var directory = fixture.Paths.RecordsDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "index.m3u8"), Playlist("chunk_00000.m4s"));
        await TranscodeArtifactPolicy.ValidatePlaylistAsync(directory, Files(), CancellationToken.None);
    }

    /// <summary>派生成品只按物理文件统计一次，并独立于原录像和临时上传空间。</summary>
    [Fact]
    public async Task StorageCountsDerivedFilesOnceInTheirOwnCategory()
    {
        using var fixture = new StorageUsageTestContext();
        await fixture.WriteAsync(fixture.Paths.RecordsDirectory, ".derived/asset/version/recipe/chunk_00000.m4s", 29);
        await fixture.WriteAsync(fixture.Paths.RecordsDirectory, ".work/transcoding/job/1/chunk_00000.m4s", 11);
        var report = await fixture.Service.GetDetailsAsync();
        var volume = Assert.Single(report.Volumes, item => item.Id == "recordings");
        Assert.Equal(29, volume.DerivedBytes);
        Assert.Equal(0, volume.RecordingBytes);
        Assert.Equal(29, Assert.Single(report.Directories, item => item.Kind == "derived").TotalBytes);
        Assert.Equal(40, report.Directories.Sum(item => item.TotalBytes));
        Assert.Equal(volume.UsedBytes, volume.RecordingBytes + volume.ClipBytes + volume.DerivedBytes + volume.OtherBytes);
    }

    private static string Playlist(string uri) => "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.0,\n" + uri + "\n#EXT-X-ENDLIST\n";

    private static List<TranscodeArtifact> Files() => [new() { Name = "index.m3u8" }, new() { Name = "init.mp4" }, new() { Name = "chunk_00000.m4s" }];
}
