using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证旧归档头像补齐、归组序号及表情来源只能来自归档。</summary>
public sealed class EventArtworkServiceTests
{
    [Fact]
    public async Task HistoricalMessageWithoutAvatarResolvesUidWithoutChangingArchive()
    {
        await using var context = new EventArtworkTestContext();
        await context.WriteAsync(new() { Sequence = 1, UserId = "42", Text = "旧归档" });

        using var image = await context.Artwork.GetAvatarAsync(context.Session, 1);

        Assert.NotNull(image);
        Assert.Equal("/users/42.png", await File.ReadAllTextAsync(image.Path));
        Assert.Equal(1, context.Source.Calls[42]);
        var archive = await File.ReadAllTextAsync(Path.Combine(context.Paths.RecordsDirectory, "session", "弹幕", "事件.jsonl"));
        Assert.DoesNotContain("users/42", archive);
    }

    [Fact]
    public async Task ArchivedAvatarDoesNotQueryUserProfile()
    {
        await using var context = new EventArtworkTestContext();
        await context.WriteAsync(new() { Sequence = 1, UserId = "42", AvatarUrl = "https://i0.hdslb.com/archived.png" });

        using var image = await context.Artwork.GetAvatarAsync(context.Session, 1);

        Assert.NotNull(image);
        Assert.Equal("/archived.png", await File.ReadAllTextAsync(image.Path));
        Assert.Empty(context.Source.Calls);
    }

    [Fact]
    public async Task EmoticonUsesOriginalArrayIndexAndPreservesContentType()
    {
        await using var context = new EventArtworkTestContext();
        await context.WriteAsync(new()
        {
            Sequence = 1,
            Payload = new JsonObject
            {
                ["emoticons"] = new JsonArray
                {
                    new JsonObject { ["text"] = "[长表情文字]", ["url"] = "https://i0.hdslb.com/first.png" },
                    new JsonObject { ["text"] = "[笑]", ["url"] = "https://i0.hdslb.com/second.png" }
                }
            }
        });

        using var image = await context.Artwork.GetEmoticonAsync(context.Session, 1, 1);

        Assert.NotNull(image);
        Assert.Equal("/second.png", await File.ReadAllTextAsync(image.Path));
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("/second.png", Assert.Single(context.Http.Requests).Source.AbsolutePath);
        Assert.Null(await context.Artwork.GetEmoticonAsync(context.Session, 1, -1));
        Assert.Null(await context.Artwork.GetEmoticonAsync(context.Session, 1, 2));
    }

    [Fact]
    public async Task GroupedSequenceResolvesItsOriginalArchive()
    {
        await using var context = new EventArtworkTestContext();
        context.Session.EventSources.Add(new() { DirectoryPath = "source", StartOffsetMs = 30000 });
        await context.WriteAsync(new() { Sequence = 1, UserId = "42", OffsetMs = 1000 });

        using var image = await context.Artwork.GetAvatarAsync(context.Session, 1_000_000_000_001);

        Assert.NotNull(image);
        Assert.Equal("/users/42.png", await File.ReadAllTextAsync(image.Path));
        Assert.Null(await context.Artwork.GetAvatarAsync(context.Session, 1));
    }

    [Fact]
    public async Task MissingSequenceNeverReturnsAnotherMessagesArtwork()
    {
        await using var context = new EventArtworkTestContext();
        await context.WriteAsync(new() { Sequence = 2, AvatarUrl = "https://i0.hdslb.com/wrong-message.png" });

        Assert.Null(await context.Artwork.GetAvatarAsync(context.Session, 1));
        Assert.Empty(context.Http.Requests);
    }

    [Theory]
    [InlineData("https://outside.example/emote.png")]
    [InlineData("file:///etc/passwd")]
    public async Task DisallowedArchivedEmoticonNeverFetchesExternalSource(string source)
    {
        await using var context = new EventArtworkTestContext();
        await context.WriteAsync(new() { Sequence = 1, Payload = new JsonObject { ["emoticons"] = new JsonArray { new JsonObject { ["url"] = source } } } });

        Assert.Null(await context.Artwork.GetEmoticonAsync(context.Session, 1, 0));
        Assert.Empty(context.Http.Requests);
    }
}
