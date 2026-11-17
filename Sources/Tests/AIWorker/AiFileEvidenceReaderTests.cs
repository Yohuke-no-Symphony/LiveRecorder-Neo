using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

/// <summary>验证固定本地证据的分页和受限路径，不通过主站逐次查询模型工具。</summary>
public sealed class AiFileEvidenceReaderTests
{
    /// <summary>过滤条件和去重后仍以真实文件行游标继续下一页。</summary>
    [Fact]
    public async Task PagesFilteredEvidenceUsingStableLineCursors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ai-evidence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var evidence = new[]
            {
                new AiEvidence { Id = "first", Kind = "Danmu", OffsetMs = 10, Text = "歌曲第一条" },
                new AiEvidence { Id = "duplicate", Kind = "Danmu", OffsetMs = 20, Text = "歌曲第一条" },
                new AiEvidence { Id = "second", Kind = "SuperChat", OffsetMs = 30, Text = "歌曲第二条" }
            };
            await File.WriteAllLinesAsync(Path.Combine(directory, "evidence.jsonl"), evidence.Select(item => JsonSerializer.Serialize(item)));
            var reader = new AiFileEvidenceReader(directory);
            var snapshot = new AiAnalysisSnapshot { DurationMs = 100, EvidenceRelativePath = "evidence.jsonl" };

            var first = await reader.ReadAsync(snapshot, 0, 100, "All", "歌曲", null, 1, CancellationToken.None);
            var second = await reader.ReadAsync(snapshot, 0, 100, "All", "歌曲", first.NextCursor, 1, CancellationToken.None);

            Assert.Equal("first", Assert.Single(first.Items).Id);
            Assert.True(first.Truncated);
            Assert.Equal("second", Assert.Single(second.Items).Id);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>远程下发的快照路径不能越过节点工作区。</summary>
    [Fact]
    public async Task SnapshotCannotReadOutsideTheWorkspace()
    {
        var reader = new AiFileEvidenceReader(Path.GetTempPath());
        var snapshot = new AiAnalysisSnapshot { DurationMs = 100, EvidenceRelativePath = "../outside.jsonl" };

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(snapshot, 0, 100, null, null, null, 1, CancellationToken.None));
    }
}
