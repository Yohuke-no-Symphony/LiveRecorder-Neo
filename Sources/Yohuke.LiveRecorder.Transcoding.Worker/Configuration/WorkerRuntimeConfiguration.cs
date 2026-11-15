using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed record WorkerRuntimeConfiguration(WorkerConfiguration Configuration, WorkerCapabilities Capabilities, int Revision);
