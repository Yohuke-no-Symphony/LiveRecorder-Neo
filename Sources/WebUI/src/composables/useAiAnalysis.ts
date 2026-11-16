import { computed, onScopeDispose, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { AiAnalysisAPI } from '@/apis/AiAnalysisAPI'
import { ApiError } from '@/apis/APIBase'
import type { AiAnalysisStatus } from '@/models/aiAnalysis'
import type { AiAnalysisOptions } from '@/models/aiAnalysisOptions'

export function useAiAnalysis(sessionId: MaybeRefOrGetter<string>, ended: MaybeRefOrGetter<boolean>, options: AiAnalysisOptions = {})
{
  const { t } = useI18n()
  const status = ref<AiAnalysisStatus>()
  const loading = ref(false)
  const busy = ref(false)
  const showPartial = ref(false)
  const initiatedJobId = ref('')
  const loadError = ref('')
  const actionError = ref('')
  const error = computed(() => actionError.value || loadError.value)
  const job = computed(() => status.value?.currentJob)
  const running = computed(() => job.value?.state === 'Queued' || job.value?.state === 'Running')
  const hasResult = computed(() => Boolean(status.value?.latestCompleted || status.value?.latestPartial || (options.purpose === 'LiveMarkers' && job.value)))
  const hasBothResults = computed(() => Boolean(status.value?.latestCompleted && status.value?.latestPartial))
  const allowed = computed(() => toValue(options.canGenerate) !== false && toValue(options.active) !== false)
  const canGenerate = computed(() => Boolean(status.value?.enabled && status.value.canGenerate) && allowed.value)
  const canStart = computed(() => canGenerate.value && toValue(ended) && !busy.value && !loading.value && !running.value)
  const canCancel = computed(() => Boolean(status.value?.canGenerate) && allowed.value && running.value && !busy.value && !job.value?.cancelRequested && job.value?.phase !== 'Publishing')
  const result = computed(() => showPartial.value
    ? status.value?.latestPartial ?? status.value?.latestCompleted
    : status.value?.latestCompleted ?? status.value?.latestPartial)
  const resultStale = computed(() => Boolean(result.value && (result.value === status.value?.latestPartial
    ? status.value?.latestPartialStale ?? status.value?.stale
    : status.value?.latestCompletedStale ?? status.value?.stale)))
  const topics = computed(() => result.value?.topics ?? [])
  let contextRevision = 0
  let requestRevision = 0
  let controller: AbortController | undefined
  let timer: ReturnType<typeof setTimeout> | undefined
  let disposed = false

  function stopReading(): void
  {
    requestRevision++
    clearTimeout(timer)
    controller?.abort()
    controller = undefined
  }

  function isCurrent(context: number): boolean
  {
    return !disposed && context === contextRevision
  }

  async function refresh(): Promise<void>
  {
    const id = toValue(sessionId)
    if (!id || disposed || busy.value || toValue(options.active) === false)
    {
      return
    }
    stopReading()
    const context = contextRevision
    const request = requestRevision
    controller = new AbortController()
    loading.value = !status.value
    loadError.value = ''
    try
    {
      const value = await (options.purpose === 'LiveMarkers' ? AiAnalysisAPI.liveMarkerStatus(id, controller.signal) : AiAnalysisAPI.status(id, controller.signal))
      if (isCurrent(context) && request === requestRevision)
      {
        status.value = value
      }
    }
    catch (value)
    {
      if (isCurrent(context) && request === requestRevision && !(value instanceof DOMException && value.name === 'AbortError'))
      {
        if (value instanceof ApiError && [401, 403, 404].includes(value.status))
        {
          status.value = undefined
        }
        loadError.value = value instanceof Error ? value.message : t('replay.aiLoadFailed')
      }
    }
    finally
    {
      if (isCurrent(context) && request === requestRevision)
      {
        controller = undefined
        loading.value = false
        if (toValue(options.active) !== false && (!options.retainSnapshot || running.value))
        {
          timer = setTimeout(() => void refresh(), running.value ? 3000 : 30000)
        }
      }
    }
  }

  async function generate(): Promise<void>
  {
    if (!canStart.value)
    {
      return
    }
    const id = toValue(sessionId)
    const context = contextRevision
    const force = hasResult.value
    stopReading()
    busy.value = true
    actionError.value = ''
    try
    {
      const value = await (options.purpose === 'LiveMarkers' ? AiAnalysisAPI.generateLiveMarkers(id, force) : AiAnalysisAPI.generate(id, force))
      if (isCurrent(context))
      {
        initiatedJobId.value = value.id
        if (status.value)
        {
          status.value.currentJob = value
        }
        showPartial.value = true
      }
    }
    catch (value)
    {
      if (isCurrent(context))
      {
        actionError.value = value instanceof Error ? value.message : t('replay.aiFailed')
      }
    }
    finally
    {
      if (isCurrent(context))
      {
        busy.value = false
        await refresh()
      }
    }
  }

  async function cancel(): Promise<void>
  {
    if (!canCancel.value || !job.value)
    {
      return
    }
    const jobId = job.value.id
    const context = contextRevision
    stopReading()
    busy.value = true
    actionError.value = ''
    try
    {
      await AiAnalysisAPI.cancel(jobId)
    }
    catch (value)
    {
      if (isCurrent(context))
      {
        actionError.value = value instanceof Error ? value.message : t('replay.aiFailed')
      }
    }
    finally
    {
      if (isCurrent(context))
      {
        busy.value = false
        await refresh()
      }
    }
  }

  async function retry(): Promise<void>
  {
    actionError.value = ''
    await refresh()
  }

  watch([() => toValue(sessionId), () => options.retainSnapshot ? undefined : toValue(options.active), () => toValue(options.scope)], () =>
  {
    contextRevision++
    stopReading()
    status.value = undefined
    loading.value = false
    busy.value = false
    showPartial.value = false
    initiatedJobId.value = ''
    loadError.value = ''
    actionError.value = ''
    void refresh()
  }, { immediate: true, flush: 'sync' })

  watch(() => toValue(options.active), active =>
  {
    if (!options.retainSnapshot)
    {
      return
    }
    if (active === false)
    {
      clearTimeout(timer)
    }
    else if (!controller && (!status.value || running.value))
    {
      void refresh()
    }
  }, { flush: 'sync' })

  onScopeDispose(() =>
  {
    disposed = true
    contextRevision++
    stopReading()
  })

  return { status, loading, busy, error, showPartial, initiatedJobId, job, running, hasResult, hasBothResults, canGenerate, canStart, canCancel, result, resultStale, topics, refresh: retry, generate, cancel }
}
