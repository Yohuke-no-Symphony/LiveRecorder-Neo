namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal readonly record struct WorkerTransferProgress(long BytesTransferred, long TotalBytes,
    string? CurrentFile, int FilesCompleted, int FilesTotal);
