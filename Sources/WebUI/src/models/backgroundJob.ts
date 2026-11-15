import type { TranscodeJob } from './transcoding'
import type { AiAnalysisJob } from './aiAnalysis'
import type { MediaJob } from './media'

export type BackgroundJob =
  | { kind: 'media'; job: MediaJob }
  | { kind: 'ai'; job: AiAnalysisJob }
  | { kind: 'transcode'; job: TranscodeJob }
