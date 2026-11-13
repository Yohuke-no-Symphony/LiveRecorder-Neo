using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Server.Endpoints;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class EventArtworkHttpContext : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recorder-artwork-http-" + Guid.NewGuid().ToString("N"));
    internal EventArtworkHttpAvatarSource Source { get; } = new();
    internal WebApplication App { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;
    internal static byte[] ImageBytes => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB8UAAAAASUVORK5CYII=");

    internal static async Task<EventArtworkHttpContext> CreateAsync()
    {
        var context = new EventArtworkHttpContext();
        try
        {
            var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0", "--environment", "Production", "--Logging:LogLevel:Default", "Critical"]);
            builder.Services.AddAuthentication("ArtworkTest").AddScheme<AuthenticationSchemeOptions, EventArtworkTestAuthenticationHandler>("ArtworkTest", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IAppPaths>(new AppPaths(context.directory, Path.Combine(context.directory, "records")));
            builder.Services.AddSingleton<IUserAvatarSource>(context.Source);
            builder.Services.AddSingleton<AudienceAvatarService>();
            builder.Services.AddSingleton<ArtworkCache>();
            builder.Services.AddSingleton<EventArchiveReader>();
            builder.Services.AddSingleton<SessionEventReader>();
            builder.Services.AddSingleton<EventArtworkService>();
            builder.Services.AddSingleton<LibraryService>(_ => throw new InvalidOperationException("Session access must not run before authorization."));
            builder.Services.AddHttpClient("artwork");
            context.App = builder.Build();
            context.App.UseAuthentication();
            context.App.UseAuthorization();
            context.App.MapEventArtworkEndpoints();
            await context.App.StartAsync();
            var address = context.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            context.Client = new() { BaseAddress = new(address) };
            var paths = context.App.Services.GetRequiredService<IAppPaths>();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EventArtworkHttpAvatarSource.AVATAR_URL))).ToLowerInvariant();
            var cache = Path.Combine(paths.CacheDirectory, "artwork");
            Directory.CreateDirectory(cache);
            var path = Path.Combine(cache, key + ".image");
            await File.WriteAllBytesAsync(path, ImageBytes);
            await File.WriteAllTextAsync(path + ".type", "image/png");
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.DisposeAsync();
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
