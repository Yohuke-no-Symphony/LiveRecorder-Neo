using System.Diagnostics;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Server.Contracts;
using Yohuke.LiveRecorder.Server.Configuration;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>映射首次安装，安装后不可重新初始化。</summary>
public static class SetupEndpoints
{
    /// <summary>注册安装端点。</summary>
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/status", (IInstallationService installation) => Results.Ok(new { installed = installation.IsInstalled, requiresToken = !installation.IsInstalled }));
        endpoints.MapPost("/api/setup", async (SetupRequest request, IInstallationService installation, ISettingsStore settingsStore, WebUiLocation webUi, CancellationToken cancellationToken) =>
        {
            if (installation.IsInstalled)
            {
                return Results.Conflict(new { detail = "安装已完成。" });
            }
            webUi.EnsurePrivate(request.StoragePath);
            var ffmpeg = string.IsNullOrWhiteSpace(request.FfmpegPath) ? "ffmpeg" : request.FfmpegPath;
            var ffprobe = string.IsNullOrWhiteSpace(request.FfprobePath) ? "ffprobe" : request.FfprobePath;
            await VerifyToolAsync(ffmpeg, cancellationToken);
            await VerifyToolAsync(ffprobe, cancellationToken);
            await installation.InstallAsync(new InstallRequest(request.Token, request.Email, request.Password, request.UserName, request.StoragePath, request.MongoConnectionString, request.DatabaseName), cancellationToken);
            var settings = await settingsStore.GetAsync(cancellationToken);
            settings.FfmpegPath = ffmpeg;
            settings.FfprobePath = ffprobe;
            await settingsStore.SaveAsync(settings, cancellationToken);
            return Results.Ok(new { installed = true });
        });
        return endpoints;
    }

    private static async Task VerifyToolAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start) ?? throw new InfrastructureException("媒体工具无法启动。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                throw;
            }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
            {
                throw new InfrastructureException("媒体工具验证失败。");
            }
        }
        catch (Exception error) when (error is not InfrastructureException && error is not OperationCanceledException)
        {
            throw new InfrastructureException("找不到可运行的 FFmpeg/ffprobe，请检查路径。");
        }
    }
}
