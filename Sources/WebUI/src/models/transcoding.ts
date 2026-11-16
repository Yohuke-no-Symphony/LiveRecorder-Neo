import type { Entity } from './media'

export const ORIGINAL_QUALITY_ID = 'original'
export const MIN_TRANSCODE_PRIORITY = -2147483648
export const MAX_TRANSCODE_PRIORITY = 2147483647

export type WorkerKind = 'Transcoding' | 'Clipping'

export interface TranscodingProfile
{
  id: string
  name: string
  height: number
  videoBitrateKbps: number
  maxFrameRate: number | null
  audioBitrateKbps: number
  segmentDurationSeconds: number
  keyframeIntervalSeconds: number
  singleFile: boolean
  enabled: boolean
  autoTranscode: boolean
  priority: number
}

export interface TranscodingSettings
{
  enabled: boolean
  profiles: TranscodingProfile[]
  qualityOrder: string[]
  defaultPlaybackQualityId: string
}

export interface WorkerConfiguration
{
  ffmpegPath: string
  ffprobePath: string
  tempDirectory: string
  maxConcurrentEncodes: number
  gpuConcurrency: number
  cpuConcurrency: number
  cpuFallbackMode: 'Balanced' | 'Disabled'
  gpuWaitSeconds: number
  cpuLoadThreshold: number
  cpuSampleWindowSeconds: number
  cpuThreadFraction: number
  preferredEncoder: string
  deviceId: string | null
  maxCacheBytes: number
  minimumFreeBytes: number
  downloadBytesPerSecond: number
  uploadBytesPerSecond: number
}

export interface PlaybackQuality
{
  id: string
  name: string
  available: boolean
  height?: number
  state: string
}

export interface TranscodingWorker
{
  id: string
  kind?: WorkerKind
  name: string
  enabled: boolean
  configuration: WorkerConfiguration
  configurationRevision: number
  appliedConfigurationRevision: number
  capabilities?: { operatingSystem: string; architecture: string; toolVersion: string; clippingVersion?: number; encoders: { encoder: string; deviceId: string | null; available: boolean; error?: string | null }[] } | null
  telemetry?: { cpuPercent: number | null; cpuThreadBudget: number; runningGpu: number; runningCpu: number; runningJobs?: number; freeBytes: number; cpuUnavailableReason?: string | null } | null
  lastSeenAtUtc: string | null
  configurationError: string | null
}

export interface TranscodeJob extends Entity
{
  sessionId: string
  title: string
  profileName: string
  state: string
  stage: string
  progress: number
  workerId?: string | null
  encoder?: string | null
  activity?: WorkerJobActivity | null
  lastProgressAtUtc?: string | null
  error?: string | null
  finishedAtUtc?: string | null
  cancelRequested?: boolean
}

export interface WorkerJobActivity
{
  bytesTransferred?: number | null
  totalBytes?: number | null
  bytesPerSecond?: number | null
  processedDurationMs?: number | null
  totalDurationMs?: number | null
  framesPerSecond?: number | null
  speed?: number | null
  estimatedSecondsRemaining?: number | null
  currentFile?: string | null
  filesCompleted?: number | null
  filesTotal?: number | null
}

export interface WorkerLogEntry
{
  id: string
  workerId: string
  streamId: string
  sequence: number
  cursor: number
  jobId?: string | null
  attempt?: number | null
  timestampUtc: string
  receivedAtUtc: string
  level: string
  message: string
  droppedCount?: number
  stage?: string | null
  progress?: number | null
}

export interface WorkerLogPage
{
  entries: WorkerLogEntry[]
  nextCursor: number | null
  hasMore: boolean
}

export function defaultTranscodingSettings(): TranscodingSettings
{
  return {
    enabled: true,
    profiles: [{
      id: '720p',
      name: '720p',
      height: 720,
      videoBitrateKbps: 2500,
      maxFrameRate: null,
      audioBitrateKbps: 128,
      segmentDurationSeconds: 6,
      keyframeIntervalSeconds: 2,
      singleFile: false,
      enabled: true,
      autoTranscode: true,
      priority: 0,
    }],
    qualityOrder: [ORIGINAL_QUALITY_ID, '720p'],
    defaultPlaybackQualityId: '720p',
  }
}

export function normalizeTranscodingSettings(value?: Partial<TranscodingSettings> | null): TranscodingSettings
{
  const defaults = defaultTranscodingSettings()
  if (!value) { return defaults }
  const profiles = (value.profiles ?? defaults.profiles).map(profile => ({
    ...profile,
    maxFrameRate: profile.maxFrameRate ?? null,
    segmentDurationSeconds: profile.segmentDurationSeconds ?? 6,
    keyframeIntervalSeconds: profile.keyframeIntervalSeconds ?? 2,
    singleFile: profile.singleFile ?? false,
    autoTranscode: profile.autoTranscode ?? true,
    priority: profile.priority === undefined ? 0 : profile.priority,
  }))
  const ids = new Set([ORIGINAL_QUALITY_ID, ...profiles.map(profile => profile.id)])
  const qualityOrder = [...new Set(value.qualityOrder ?? defaults.qualityOrder)].filter(id => ids.has(id))
  for (const id of ids) { if (!qualityOrder.includes(id)) { qualityOrder.push(id) } }
  const requested = value.defaultPlaybackQualityId ?? defaults.defaultPlaybackQualityId
  const defaultPlaybackQualityId = requested === ORIGINAL_QUALITY_ID || profiles.some(profile => profile.id === requested && profile.enabled) ? requested : ORIGINAL_QUALITY_ID
  return { enabled: value.enabled ?? defaults.enabled, profiles, qualityOrder, defaultPlaybackQualityId }
}

export function isValidTranscodePriority(value: unknown): value is number
{
  return typeof value === 'number' && Number.isInteger(value) && value >= MIN_TRANSCODE_PRIORITY && value <= MAX_TRANSCODE_PRIORITY
}

export function isValidTranscodingSettings(settings: TranscodingSettings): boolean
{
  const integer = (value: number, min: number, max: number) => Number.isInteger(value) && value >= min && value <= max
  const ids = settings.profiles.map(profile => profile.id)
  return ids.length <= 12 && new Set(ids).size === ids.length && !ids.includes(ORIGINAL_QUALITY_ID)
    && settings.profiles.every(profile => /^[a-zA-Z0-9_-]{1,64}$/.test(profile.id) && !!profile.name.trim() && profile.name.length <= 80
      && integer(profile.height, 64, 8640) && profile.height % 2 === 0 && integer(profile.videoBitrateKbps, 32, 1000000)
      && (profile.maxFrameRate === null || Number.isFinite(profile.maxFrameRate) && profile.maxFrameRate > 0 && profile.maxFrameRate <= 240)
      && integer(profile.audioBitrateKbps, 16, 512)
      && Number.isFinite(profile.segmentDurationSeconds) && profile.segmentDurationSeconds >= .1 && profile.segmentDurationSeconds <= 300
      && Number.isFinite(profile.keyframeIntervalSeconds) && profile.keyframeIntervalSeconds >= .1 && profile.keyframeIntervalSeconds <= 300
      && typeof profile.singleFile === 'boolean' && typeof profile.autoTranscode === 'boolean'
      && isValidTranscodePriority(profile.priority))
    && settings.qualityOrder.length === ids.length + 1 && new Set(settings.qualityOrder).size === ids.length + 1
    && [ORIGINAL_QUALITY_ID, ...ids].every(id => settings.qualityOrder.includes(id))
    && (settings.defaultPlaybackQualityId === ORIGINAL_QUALITY_ID || settings.profiles.some(profile => profile.id === settings.defaultPlaybackQualityId && profile.enabled))
}

export function defaultWorkerConfiguration(): WorkerConfiguration
{
  return { ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', tempDirectory: '', maxConcurrentEncodes: 2, gpuConcurrency: 1, cpuConcurrency: 1, cpuFallbackMode: 'Balanced', gpuWaitSeconds: 60, cpuLoadThreshold: 60, cpuSampleWindowSeconds: 30, cpuThreadFraction: .5, preferredEncoder: 'auto', deviceId: null, maxCacheBytes: 20 * 1024 ** 3, minimumFreeBytes: 2 * 1024 ** 3, downloadBytesPerSecond: 0, uploadBytesPerSecond: 0 }
}

export function isValidWorkerConfiguration(value: WorkerConfiguration, kind: WorkerKind = 'Transcoding'): boolean
{
  const integer = (number: number, min: number, max = Number.MAX_SAFE_INTEGER) => Number.isSafeInteger(number) && number >= min && number <= max
  const commonValid = !!value.ffmpegPath.trim() && !!value.ffprobePath.trim() && value.ffmpegPath.length <= 4096 && value.ffprobePath.length <= 4096 && value.tempDirectory.length <= 4096
    && integer(value.maxConcurrentEncodes, 1, 64)
    && integer(value.maxCacheBytes, 1024 ** 2) && integer(value.minimumFreeBytes, 0) && integer(value.downloadBytesPerSecond, 0) && integer(value.uploadBytesPerSecond, 0)
  if (kind === 'Clipping') { return commonValid }
  return commonValid && integer(value.gpuConcurrency, 0, 64) && integer(value.cpuConcurrency, 0, 64) && value.gpuConcurrency + value.cpuConcurrency > 0
    && ['Balanced', 'Disabled'].includes(value.cpuFallbackMode) && integer(value.gpuWaitSeconds, 0, 86400) && integer(value.cpuSampleWindowSeconds, 5, 300)
    && Number.isFinite(value.cpuLoadThreshold) && value.cpuLoadThreshold > 0 && value.cpuLoadThreshold <= 100
    && Number.isFinite(value.cpuThreadFraction) && value.cpuThreadFraction > 0 && value.cpuThreadFraction <= 1
    && ['auto', 'h264_qsv', 'h264_nvenc', 'h264_amf', 'h264_vaapi', 'h264_videotoolbox'].includes(value.preferredEncoder) && (value.deviceId?.length ?? 0) <= 1024
}
