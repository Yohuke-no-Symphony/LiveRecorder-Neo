using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>为管理员扫描快照补充身份及文件归属，不修改缓存快照。</summary>
public sealed class StorageUsageMetadataService(IMongoContext context, IAppPaths paths)
{
    /// <summary>补充直播间、用户名称及实际转码和回收文件归属，不读取凭据字段。</summary>
    public async Task<StorageUsageReport> EnrichAsync(StorageUsageReport report, CancellationToken cancellationToken = default)
    {
        var roomsTask = context.Collection<Streamer>("streamers").Find(FilterDefinition<Streamer>.Empty)
            .Project(streamer => new StorageRoomUsage
            {
                Id = streamer.DirectoryName,
                Name = streamer.Name,
                RoomId = streamer.RoomId
            })
            .ToListAsync(cancellationToken);
        var usersTask = context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty)
            .Project(user => new StorageUserUsage
            {
                Id = user.Id,
                Name = user.DisplayName
            })
            .ToListAsync(cancellationToken);
        var trashTask = context.Collection<TrashEntry>("trash").Find(FilterDefinition<TrashEntry>.Empty)
            .Project(entry => new TrashEntry
            {
                Id = entry.Id,
                Kind = entry.Kind,
                OwnerId = entry.OwnerId,
                RetainedInPlace = entry.RetainedInPlace,
                OriginalRelativePath = entry.OriginalRelativePath
            }).ToListAsync(cancellationToken);
        var rooms = report.Rooms.ToDictionary(room => room.Id, room => room with { });
        var derivedTask = AddDerivedUsageAsync(report.DerivedBytesByAssetId, rooms, cancellationToken);
        await Task.WhenAll(roomsTask, usersTask, trashTask, derivedTask);
        var identities = (await roomsTask).Where(room => !string.IsNullOrEmpty(room.Id))
            .GroupBy(room => room.Id).ToDictionary(group => group.Key, group => group.First());
        var userNames = (await usersTask).ToDictionary(user => user.Id, user => user.Name);
        var users = report.Users.ToDictionary(user => user.Id, user => user with { });
        var trashDirectories = report.Directories.Where(directory => directory.Kind == "trash")
            .ToDictionary(directory => directory.Id, directory => directory.TotalBytes);

        foreach (var entry in await trashTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trashBytes = trashDirectories.GetValueOrDefault("recordings:.trash/" + entry.Id);
            if (entry.Kind is "Session" or "Segment")
            {
                var directory = entry.OriginalRelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (string.IsNullOrEmpty(directory) || directory.StartsWith('.'))
                {
                    continue;
                }
                var room = GetOrAddRoom(rooms, directory);
                room.TrashBytes += trashBytes;
                room.RetainedInPlace |= entry.Kind == "Segment"
                    ? entry.RetainedInPlace
                    : Directory.Exists(Path.Combine(paths.RecordsDirectory, entry.OriginalRelativePath));
            }
            else if (entry.Kind == "Clip" && trashBytes > 0 && !string.IsNullOrEmpty(entry.OwnerId))
            {
                if (!users.TryGetValue(entry.OwnerId, out var user))
                {
                    user = new()
                    {
                        Id = entry.OwnerId,
                        OwnerId = entry.OwnerId,
                        Name = entry.OwnerId,
                        Path = paths.RecordsDirectory
                    };
                    users.Add(entry.OwnerId, user);
                }
                user.TrashBytes += trashBytes;
                var directory = entry.OriginalRelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (!string.IsNullOrEmpty(directory) && !directory.StartsWith('.'))
                {
                    GetOrAddRoom(rooms, directory).TrashBytes += trashBytes;
                }
            }
        }

        foreach (var room in rooms.Values)
        {
            if (identities.TryGetValue(room.Id, out var identity))
            {
                room.Name = identity.Name;
                room.RoomId = identity.RoomId;
            }
        }
        foreach (var user in users.Values)
        {
            if (userNames.TryGetValue(user.Id, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                user.Name = name;
            }
        }

        return report with
        {
            Rooms = rooms.Values.OrderByDescending(room => room.TotalBytes).ThenBy(room => room.Id).ToArray(),
            Users = users.Values.OrderByDescending(user => user.TotalBytes).ThenBy(user => user.Id).ToArray()
        };
    }

    private async Task AddDerivedUsageAsync(IReadOnlyDictionary<string, long> bytesByAssetId,
        Dictionary<string, StorageRoomUsage> rooms, CancellationToken cancellationToken)
    {
        foreach (var assetIds in bytesByAssetId.Keys.Chunk(1000))
        {
            var assets = await context.Collection<MediaAsset>("mediaAssets").Find(asset => assetIds.Contains(asset.Id))
                .Project(asset => new { asset.Id, asset.SourceRelativePath })
                .ToListAsync(cancellationToken);
            foreach (var asset in assets)
            {
                var source = asset.SourceRelativePath;
                var separator = source.IndexOfAny(['/', '\\']);
                if (separator <= 0 || source.StartsWith('.'))
                {
                    continue;
                }
                GetOrAddRoom(rooms, source[..separator]).DerivedBytes += bytesByAssetId[asset.Id];
            }
        }
    }

    private StorageRoomUsage GetOrAddRoom(Dictionary<string, StorageRoomUsage> rooms, string directory)
    {
        if (!rooms.TryGetValue(directory, out var room))
        {
            room = new()
            {
                Id = directory,
                Name = directory,
                Path = Path.Combine(paths.RecordsDirectory, directory)
            };
            rooms.Add(directory, room);
        }
        return room;
    }
}
