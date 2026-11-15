import { computed, onScopeDispose, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AiAnalysisAPI } from '@/apis/AiAnalysisAPI'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import type { TranscodeJob } from '@/models/transcoding'
import { MediaAPI } from '@/apis/MediaAPI'
import type { AiAnalysisJob } from '@/models/aiAnalysis'
import type { BackgroundJob } from '@/models/backgroundJob'
import type { MediaJob } from '@/models/media'
import { useAuthStore } from '@/stores/auth'
import { usePolling } from './usePolling'

export function useBackgroundJobs()
{
  const auth = useAuthStore()
  const { t } = useI18n()
  const mediaJobs = ref<MediaJob[]>([])
  const transcodeJobs = ref<TranscodeJob[]>([])
  const transcodeError = ref('')
  const aiJobs = ref<AiAnalysisJob[]>([])
  const loading = ref(false)
  const actionKey = ref('')
  const mediaError = ref('')
  const aiError = ref('')
  const actionError = ref('')
  const actionErrorKey = ref('')
  const visibleAiJobs = computed(() => aiJobs.value.filter(job => job.purpose === 'LiveMarkers' ? auth.canViewRecordings : auth.canViewAnalytics))
  const error = computed(() => [actionError.value, mediaError.value, aiError.value, transcodeError.value].filter(Boolean).join(' · '))
  const jobs = computed<BackgroundJob[]>(() =>
  {
    const values: BackgroundJob[] = mediaJobs.value.map(job => ({ kind: 'media', job }))
    values.push(...visibleAiJobs.value.map(job => ({ kind: 'ai' as const, job })))
    if (auth.isAdmin) { values.push(...transcodeJobs.value.map(job => ({ kind: 'transcode' as const, job }))) }
    return values.sort((a, b) => b.job.createdAtUtc.localeCompare(a.job.createdAtUtc))
  })
  const identity = computed(() => JSON.stringify([auth.user?.id, auth.user?.status, auth.user?.role, [...(auth.user?.permissions ?? [])].sort()]))
  let contextRevision = 0
  let requestRevision = 0
  let controller: AbortController | undefined
  let disposed = false

  function isCurrent(context: number): boolean
  {
    return !disposed && context === contextRevision
  }

  function errorMessage(value: unknown): string
  {
    return value instanceof Error ? value.message : t('views.jobs.loadFailed')
  }

  async function load(): Promise<void>
  {
    const request = ++requestRevision
    const context = contextRevision
    controller?.abort()
    if (!auth.hasAccess || disposed)
    {
      return
    }
    controller = new AbortController()
    loading.value = true
    mediaError.value = ''
    aiError.value = ''
    transcodeError.value = ''
    const current = () => isCurrent(context) && request === requestRevision
    const mediaLoad = MediaAPI.jobs(controller.signal).then(value =>
    {
      if (current())
      {
        mediaJobs.value = value
      }
    }).catch(value =>
    {
      if (current())
      {
        mediaJobs.value = []
        mediaError.value = errorMessage(value)
      }
    })
    const aiLoad = auth.canViewRecordings ? AiAnalysisAPI.jobs(controller.signal).then(value =>
    {
      if (current() && auth.canViewRecordings)
      {
        aiJobs.value = value
      }
    }).catch(value =>
    {
      if (current())
      {
        aiJobs.value = []
        aiError.value = errorMessage(value)
      }
    }) : Promise.resolve()
    const transcodeLoad = auth.isAdmin ? TranscodingAPI.jobs(controller.signal).then(value =>
    {
      if (current() && auth.isAdmin) { transcodeJobs.value = value }
    }).catch(value =>
    {
      if (current()) { transcodeJobs.value = []; transcodeError.value = errorMessage(value) }
    }) : Promise.resolve()
    await Promise.all([mediaLoad, aiLoad, transcodeLoad])
    if (current())
    {
      loading.value = false
    }
  }

  async function refresh(): Promise<void>
  {
    actionError.value = ''
    actionErrorKey.value = ''
    await load()
  }

  async function act(entry: BackgroundJob, action: 'cancel' | 'retry'): Promise<void>
  {
    const job = entry.job
    if (disposed || actionKey.value || !auth.hasAccess)
    {
      return
    }
    if (entry.kind === 'ai' && (action !== 'cancel' || !canGenerateAiJob(entry.job) || !['Queued', 'Running'].includes(job.state) || entry.job.cancelRequested || entry.job.phase === 'Publishing'))
    {
      return
    }
    if (entry.kind === 'media' && action === 'retry' && !(entry.job.kind === 'Clip' ? auth.canCreateClips : auth.isAdmin))
    {
      return
    }
    if (entry.kind === 'transcode' && (!auth.isAdmin || action === 'cancel' && (entry.job.cancelRequested || !['Queued', 'Running', 'Publishing'].includes(job.state)))) { return }
    const context = contextRevision
    actionKey.value = `${entry.kind}:${job.id}`
    actionError.value = ''
    actionErrorKey.value = ''
    try
    {
      if (entry.kind === 'transcode')
      {
        if (action === 'cancel') { await TranscodingAPI.cancelJob(job.id) }
        else { await TranscodingAPI.retryJob(job.id) }
      }
      else if (entry.kind === 'ai')
      {
        await AiAnalysisAPI.cancel(job.id)
      }
      else if (action === 'cancel')
      {
        await MediaAPI.cancelJob(job.id)
      }
      else
      {
        await MediaAPI.retryJob(job.id)
      }
      if (isCurrent(context))
      {
        await load()
      }
    }
    catch (value)
    {
      if (isCurrent(context))
      {
        actionError.value = errorMessage(value)
        actionErrorKey.value = `${entry.kind}:${job.id}`
      }
    }
    finally
    {
      if (isCurrent(context))
      {
        actionKey.value = ''
      }
    }
  }

  function canGenerateAiJob(job: AiAnalysisJob): boolean
  {
    return job.purpose === 'LiveMarkers' ? auth.canGenerateLiveMarkers : auth.canGenerateAnalytics
  }

  watch(identity, () =>
  {
    contextRevision++
    requestRevision++
    controller?.abort()
    mediaJobs.value = []
    aiJobs.value = []
    transcodeJobs.value = []
    loading.value = false
    actionKey.value = ''
    actionError.value = ''
    actionErrorKey.value = ''
    mediaError.value = ''
    aiError.value = ''
    transcodeError.value = ''
    void load()
  }, { flush: 'sync' })

  onScopeDispose(() =>
  {
    disposed = true
    contextRevision++
    requestRevision++
    controller?.abort()
  })
  usePolling(load, 4000)

  return { jobs, aiJobs: visibleAiJobs, loading, actionKey, error, actionError, actionErrorKey, canGenerateAiJob, refresh, act }
}
