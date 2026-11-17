using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>维护录像共享配置，以带版本的清单更新作为持久化边界。</summary>
public sealed class SessionConfigurationService(IMongoContext context, LibraryService library, ISettingsStore settingsStore, IAppPaths paths)
{
    /// <summary>读取可见录像的共享配置，优先使用已持久化的清单。</summary>
    public async Task<SessionConfigurationResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var detail = await RequireVisibleAsync(id, cancellationToken);
        var path = Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, detail.Session.DirectoryPath), "场次.json");
        var session = await SessionManifestFiles.ReadSessionAsync(path, cancellationToken) ?? detail.Session;
        return await ResponseAsync(session, cancellationToken);
    }

    /// <summary>在文件锁内验证版本并保存，后续录制和媒体任务不能覆盖新配置。</summary>
    public async Task<SessionConfigurationResponse> UpdateAsync(string id, SessionConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TranslationDanmuPatternOverride is not null)
        {
            TranslationDanmuPattern.Create(request.TranslationDanmuPatternOverride);
        }
        if (request.ExpectedRevision < 1)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, id, cancellationToken);
        var detail = await RequireVisibleAsync(id, cancellationToken);
        if (await context.Collection<TrashEntry>("trash").Find(x => x.EntityId == id && x.Kind == "Session").AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
        }
        var session = await SessionManifestFiles.SetConfigurationAsync(
            new() { Session = detail.Session, Segments = detail.Segments.ToList() },
            MediaPaths.Resolve(paths.RecordsDirectory, detail.Session.DirectoryPath),
            request.TranslationDanmuPatternOverride, request.ExpectedRevision, cancellationToken);
        await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == id,
            Builders<BroadcastSession>.Update
                .Set(x => x.TranslationDanmuPatternOverride, session.TranslationDanmuPatternOverride)
                .Set(x => x.ConfigurationRevision, session.ConfigurationRevision), cancellationToken: cancellationToken);
        return await ResponseAsync(session, cancellationToken);
    }

    private async Task<SessionDetail> RequireVisibleAsync(string id, CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(id, cancellationToken);
        if (detail.Session.IsHidden)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        }
        return detail;
    }

    private async Task<SessionConfigurationResponse> ResponseAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        return new(session.TranslationDanmuPatternOverride, TranslationDanmuPattern.Resolve(session, settings), session.ConfigurationRevision);
    }
}
