using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Media;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

internal static class PlaybackHttpMediaFixture
{
    internal static async Task<HttpServerFixture> CreateAsync(int playbackConcurrency = 2)
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
            Assert.NotNull(settings);
            settings["playbackConcurrency"] = playbackConcurrency;
            settings["minimumFreeBytes"] = 0;
            using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal static async Task<BroadcastSession> SeedRecordingAsync(HttpServerFixture fixture, int durationSeconds, bool isClosed = true,
        int bFrames = 0)
    {
        var started = DateTime.UtcNow.AddMinutes(-10);
        var session = new BroadcastSession
        {
            Title = "HTTP 播放窗口回归",
            DirectoryPath = Path.Combine("playback-http", Guid.NewGuid().ToString("N")),
            StartedAtUtc = started,
            EndedAtUtc = started.AddSeconds(durationSeconds),
            LastMediaAtUtc = started.AddSeconds(durationSeconds),
            State = "Completed"
        };
        var sample = Path.Combine(fixture.DirectoryPath, "source-" + session.Id + ".flv");
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        await RunFfmpegAsync(settings.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", bFrames > 0 ? "medium" : "ultrafast", "-bf", bFrames.ToString(CultureInfo.InvariantCulture),
            "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", sample
        ]);
        using var source = new PlaybackRecordingSource(await File.ReadAllBytesAsync(sample));
        using var client = new HttpClient(source, disposeHandler: false);
        var recorder = new FlvMediaRecorder(client, Options.Create(new MediaToolOptions()), fixture.App.Services.GetRequiredService<ISettingsStore>());
        await recorder.RecordAsync(new()
        {
            SessionId = session.Id,
            StreamUrl = "https://playback-fixture.invalid/source.flv",
            OutputDirectory = Path.Combine(fixture.StoragePath, session.DirectoryPath),
            Title = session.Title,
            StartedAtUtc = started,
            SessionStartedAtUtc = started
        }, source, CancellationToken.None);
        var committed = Assert.Single(source.Updates, update => update.Kind == "Closed");
        var segment = new RecordingSegment
        {
            SessionId = session.Id,
            RelativePath = committed.RelativePath,
            IndexRelativePath = committed.IndexRelativePath,
            StartOffsetMs = committed.StartOffsetMs,
            DurationMs = committed.DurationMs,
            CommittedBytes = committed.CommittedBytes,
            Codec = committed.Codec,
            IsClosed = isClosed
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(segment);
        return session;
    }

    internal static async Task DownloadAndDecodeAsync(HttpServerFixture fixture, PlaybackResponse playback)
    {
        Assert.NotNull(playback.Url);
        using var response = await fixture.Admin.Client.GetAsync(playback.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var playlist = await response.Content.ReadAsStringAsync();
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        var initializations = Regex.Matches(playlist, "#EXT-X-MAP:URI=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(initializations);
        var fragments = playlist.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        Assert.NotEmpty(fragments);
        var directory = Path.Combine(fixture.DirectoryPath, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baseUri = new Uri(new Uri(fixture.Address, playback.Url), ".");
        foreach (var name in initializations.Concat(fragments).Distinct(StringComparer.Ordinal))
        {
            Assert.False(Path.IsPathRooted(name));
            var destination = Path.GetFullPath(Path.Combine(directory, name));
            Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, destination);
            using var asset = await fixture.Admin.Client.GetAsync(new Uri(baseUri, name));
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            var bytes = await asset.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes);
        }
        var path = Path.Combine(directory, "index.m3u8");
        await File.WriteAllTextAsync(path, playlist);
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        await RunFfmpegAsync(settings.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-xerror", "-i", path, "-f", "null", "-"]);
    }

    internal static async Task RunFfmpegAsync(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process();
        process.StartInfo = start;
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var diagnostic = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), diagnostic, output);
            Assert.True(process.ExitCode == 0, await diagnostic);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
