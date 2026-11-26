using Microsoft.Extensions.FileProviders;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class WebUiStartupFlow(string webRoot) : IApplicationStartupFlow
{
    void IApplicationStartupFlow.Execute(WebApplication app)
    {
        if (Directory.Exists(webRoot))
        {
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
        }
        app.MapFallback(async http =>
        {
            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/hubs"))
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_ENDPOINT_NOT_FOUND, 404).ExecuteAsync(http);
                return;
            }
            var index = Path.Combine(webRoot, "index.html");
            if (!File.Exists(index))
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_WEBUI_NOT_BUILT, 503).ExecuteAsync(http);
                return;
            }
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Path.StartsWithSegments("/shares/clips"))
            {
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            await http.Response.SendFileAsync(index);
        });
    }
}
