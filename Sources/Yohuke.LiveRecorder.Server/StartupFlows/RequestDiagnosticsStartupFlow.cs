using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Localization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain.Localization;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class RequestDiagnosticsStartupFlow : IApplicationStartupFlow
{
    void IApplicationStartupFlow.Execute(WebApplication app)
    {
        var localization = new RequestLocalizationOptions
        {
            ApplyCurrentCultureToResponseHeaders = true,
            RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()]
        };
        localization.SetDefaultCulture(SupportedLanguages.DEFAULT)
            .AddSupportedCultures(SupportedLanguages.CHINESE, SupportedLanguages.ENGLISH, SupportedLanguages.JAPANESE)
            .AddSupportedUICultures(SupportedLanguages.CHINESE, SupportedLanguages.ENGLISH, SupportedLanguages.JAPANESE);
        app.UseRequestLocalization(localization);
        app.Use(async (http, next) =>
        {
            try
            {
                http.Response.Headers.XContentTypeOptions = "nosniff";
                await next();
            }
            catch (InfrastructureException error) when (!http.Response.HasStarted)
            {
                await LocalizedProblem.Create(error).ExecuteAsync(http);
            }
            catch (AntiforgeryValidationException) when (!http.Response.HasStarted)
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_CSRF_EXPIRED, 400).ExecuteAsync(http);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
            }
            catch (MongoException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("Database request failed: {Message}", error.Message);
                await LocalizedProblem.Create(MessageCode.REQUEST_DATABASE_UNAVAILABLE, 503).ExecuteAsync(http);
            }
            catch (TimeoutException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("Request timed out: {Message}", error.Message);
                await LocalizedProblem.Create(MessageCode.REQUEST_TIMEOUT, 503).ExecuteAsync(http);
            }
            catch (Exception error) when (!http.Response.HasStarted)
            {
                app.Logger.LogError(error, "Request failed");
                await LocalizedProblem.Create(MessageCode.REQUEST_FAILED, 500).ExecuteAsync(http);
            }
        });
        app.UseStatusCodePages(async context =>
        {
            if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
            {
                var status = context.HttpContext.Response.StatusCode;
                var code = status switch
                {
                    401 => MessageCode.REQUEST_UNAUTHORIZED,
                    403 => MessageCode.REQUEST_FORBIDDEN,
                    404 => MessageCode.REQUEST_ENDPOINT_NOT_FOUND,
                    429 => MessageCode.REQUEST_RATE_LIMIT,
                    _ => MessageCode.REQUEST_INVALID
                };
                await LocalizedProblem.Create(code, status).ExecuteAsync(context.HttpContext);
            }
        });
    }
}
