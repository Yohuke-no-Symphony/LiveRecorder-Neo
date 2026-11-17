import { computed, onScopeDispose, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import type { WorkerLogEntry, WorkerLogPage } from '@/models/transcoding'

export const MAX_WORKER_LOG_ENTRIES = 1000
const POLL_INTERVAL_MS = 1000

export function useWorkerLogs(identity: () => string | null, read: (after: number | undefined, signal: AbortSignal) => Promise<WorkerLogPage>)
{
  const { t } = useI18n()
  const entries = ref<WorkerLogEntry[]>([])
  const loading = ref(false)
  const failure = shallowRef<unknown>(null)
  const retrying = ref(false)
  const error = computed(() =>
  {
    const value = failure.value
    if (value === null)
    {
      return ''
    }
    if (value instanceof ApiError && (value.status === 401 || value.status === 403))
    {
      return t('transcoding.logAccessDenied')
    }
    return value instanceof Error && value.message ? value.message : t('transcoding.logLoadFailed')
  })
  let cursor: number | undefined
  let generation = 0
  let disposed = false
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined

  function stop(): void
  {
    clearTimeout(timer)
    timer = undefined
    controller?.abort()
    controller = undefined
  }

  function isCurrent(context: number): boolean
  {
    return !disposed && context === generation
  }

  async function poll(context: number): Promise<void>
  {
    if (!isCurrent(context))
    {
      return
    }
    const request = new AbortController()
    controller = request
    let continuePolling = true
    try
    {
      const page = await read(cursor, request.signal)
      if (!isCurrent(context))
      {
        return
      }
      const known = new Set(entries.value.map(entry => entry.id))
      const additions = page.entries.filter(entry =>
      {
        if (known.has(entry.id))
        {
          return false
        }
        known.add(entry.id)
        return true
      })
      if (additions.length)
      {
        entries.value = [...entries.value, ...additions].slice(-MAX_WORKER_LOG_ENTRIES)
      }
      cursor = page.nextCursor ?? cursor
      failure.value = null
      retrying.value = false
    }
    catch (value)
    {
      if (!isCurrent(context))
      {
        return
      }
      const status = value instanceof ApiError ? value.status : undefined
      const denied = status === 401 || status === 403
      continuePolling = !denied && status !== 404
      if (status === 409)
      {
        cursor = undefined
      }
      if (denied || status === 409)
      {
        entries.value = []
      }
      failure.value = value ?? undefined
      retrying.value = continuePolling
    }
    finally
    {
      if (isCurrent(context))
      {
        controller = undefined
        loading.value = false
        if (continuePolling)
        {
          timer = setTimeout(() => { void poll(context) }, POLL_INTERVAL_MS)
        }
      }
    }
  }

  watch(identity, () =>
  {
    generation++
    stop()
    cursor = undefined
    entries.value = []
    loading.value = false
    failure.value = null
    retrying.value = false
    if (identity() && !disposed)
    {
      loading.value = true
      void poll(generation)
    }
  }, { immediate: true, flush: 'sync' })

  onScopeDispose(() =>
  {
    disposed = true
    generation++
    stop()
  })

  return { entries, loading, error, retrying }
}
