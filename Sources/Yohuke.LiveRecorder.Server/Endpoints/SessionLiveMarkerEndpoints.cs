using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>场次共享直播记录的原生上传、阅读和归属管理接口。</summary>
public static class SessionLiveMarkerEndpoints
{
    /// <summary>注册观看权限约束下的记录接口，上传另需上传授权。</summary>
    public static IEndpointRouteBuilder MapSessionLiveMarkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var records = endpoints.MapGroup("/api/sessions/{id}/live-markers")
            .RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        records.MapGet("",
            async (string id, HttpContext http, ISessionLiveMarkerService service, CancellationToken ct) =>
                Results.Ok(await service.ListAsync(id, Actor(http), ct)));
        records.MapGet("/{fileId}",
            async (string id, string fileId, HttpContext http, ISessionLiveMarkerService service,
                    CancellationToken ct) =>
                Results.Ok(await service.GetAsync(id, fileId, Actor(http), ct)));
        records.MapPost("",
            async (string id, HttpContext http, ISessionLiveMarkerService service, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType)
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_INVALID);
                }

                if (http.Request.ContentLength > LiveMarkerLimits.MAXIMUM_BYTES + 65536)
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_TOO_LARGE, 413);
                }

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                {
                    feature.MaxRequestBodySize = LiveMarkerLimits.MAXIMUM_BYTES + 65536;
                }

                IFormCollection form;
                try
                {
                    form = await http.Request.ReadFormAsync(new FormOptions
                    {
                        MultipartBodyLengthLimit = LiveMarkerLimits.MAXIMUM_BYTES + 65536,
                        ValueCountLimit = 8,
                        ValueLengthLimit = 256
                    }, ct);
                }
                catch (InvalidDataException)
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_INVALID);
                }
                catch (BadHttpRequestException error) when (error.StatusCode == 413)
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_TOO_LARGE, 413);
                }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_INVALID);
                }

                if (form.Files[0].Length > LiveMarkerLimits.MAXIMUM_BYTES)
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_TOO_LARGE, 413);
                }

                var calibration = form["calibrationMs"].ToString();
                if (!long.TryParse(string.IsNullOrEmpty(calibration) ? "0" : calibration, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var calibrationMs))
                {
                    throw new InfrastructureException(MessageCode.LIVE_MARKER_ALIGNMENT_INVALID);
                }

                await using var content = form.Files[0].OpenReadStream();
                var file = await service.UploadAsync(id, content, form.Files[0].FileName, form["timeZoneId"].ToString(),
                    calibrationMs, Actor(http), ct);
                return Results.Created($"/api/sessions/{id}/live-markers/{file.Id}", file);
            }).RequireAuthorization(UserPermissionNames.UPLOAD_LIVE_MARKERS);
        records.MapPut("/{fileId}/alignment", async (string id, string fileId, LiveMarkerAlignmentRequest request,
                HttpContext http, ISessionLiveMarkerService service, CancellationToken ct) =>
            Results.Ok(await service.AlignAsync(id, fileId, request.CalibrationMs, Actor(http), ct)));
        records.MapDelete("/{fileId}",
            async (string id, string fileId, HttpContext http, ISessionLiveMarkerService service,
                CancellationToken ct) =>
            {
                await service.DeleteAsync(id, fileId, Actor(http), ct);
                return Results.NoContent();
            });
        return endpoints;
    }

    private static LiveMarkerActor Actor(HttpContext http)
    {
        return new(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, http.User.Identity?.Name ?? "",
            http.User.IsInRole("Admin") ||
            http.User.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_LIVE_MARKERS));
    }
}
