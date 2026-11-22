namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal static class HostStartupFlow
{
    internal static (WebApplicationBuilder Builder, string[] DevelopmentOrigins) Prepare(string[] args)
    {
        if (Environment.GetEnvironmentVariable("LIVE_RECORDER_DATA_DIR") is not null
            || args.Any(value => value.Equals("--DataDirectory", StringComparison.OrdinalIgnoreCase) || value.StartsWith("--DataDirectory=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The --DataDirectory option and LIVE_RECORDER_DATA_DIR environment variable are no longer supported. Use --config to specify a startup JSON file and set DataDirectory in that file.");
        }
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Configuration["DataDirectory"] is not null)
        {
            throw new InvalidOperationException("The standalone DataDirectory startup setting is no longer supported. Use --config to specify a startup JSON file and set DataDirectory in that file.");
        }
        if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:4784");
        }
        var developmentOrigins = builder.Environment.IsDevelopment()
            ? builder.Configuration.GetSection("WebUI:DevelopmentOrigins").Get<string[]>() ?? []
            : [];
        if (developmentOrigins.Length > 0)
        {
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(developmentOrigins)
                .AllowAnyHeader()
                .WithExposedHeaders("Content-Disposition")
                .AllowAnyMethod()
                .AllowCredentials()));
        }
        return (builder, developmentOrigins);
    }
}
