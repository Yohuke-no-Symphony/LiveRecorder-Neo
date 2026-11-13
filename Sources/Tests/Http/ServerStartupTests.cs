using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Server;
using Yohuke.LiveRecorder.Server.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>使用真实 Kestrel 验证宿主能启动且匿名初始化不依赖数据库。</summary>
public sealed class ServerStartupTests
{
    [Fact]
    public async Task PublicDataDirectoryIsRejectedBeforeDirectoriesOrInstallationTokensAreCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-host-tests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(AppContext.BaseDirectory, "webui", "rejected-data-" + Guid.NewGuid().ToString("N"));
        var records = Path.Combine(directory, "records");
        WebApplication? app = null;
        try
        {
            Directory.CreateDirectory(directory);
            var configFile = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new { DataDirectory = data, RecordsDirectory = records }));

            var error = Assert.Throws<InfrastructureException>(() =>
            {
                app = ServerStartup.CreateApplication(["--config", configFile, "--environment", "Production", "--urls", "http://127.0.0.1:0"]);
            });

            Assert.Equal("PATH_WEBUI_OVERLAP", error.Code);
            Assert.False(Directory.Exists(data));
            Assert.False(Directory.Exists(records));
            Assert.False(File.Exists(Path.Combine(data, "installation-token")));
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
            if (Directory.Exists(data))
            {
                Directory.Delete(data, true);
            }
        }
    }

    [Fact]
    public void PublicDirectoryComparisonMatchesThePlatformCaseRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-host-tests", Guid.NewGuid().ToString("N"));
        var webUi = new WebUiLocation(Path.Combine(directory, "WebUI"));
        var name = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? "webui" : "WebUI";

        Assert.Throws<InfrastructureException>(() => webUi.EnsurePrivate(Path.Combine(directory, name, "private-data")));
    }

    [Fact]
    public async Task ServerStartsOnDynamicPortAndIssuesAnonymousCsrf()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        using var response = await fixture.Admin.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await fixture.Admin.RefreshCsrfAsync();
        Assert.Null(me["user"]);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Admin.CsrfToken));
    }
}
