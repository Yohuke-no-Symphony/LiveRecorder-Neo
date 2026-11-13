using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

internal sealed class SessionTrashJournal
{
    public TrashEntry Entry { get; set; } = new();
    public SessionManifest Manifest { get; set; } = new();
}
