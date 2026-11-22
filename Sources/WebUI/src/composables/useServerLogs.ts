import { computed, onScopeDispose, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import { ServerLogsAPI } from '@/apis/ServerLogsAPI'
import type { ServerLogEntry, ServerLogResetReason } from '@/models/serverLogs'

export const MAX_SERVER_LOG_ENTRIES = 1000
const POLL_INTERVAL_MS = 3000
const CATCH_UP_INTERVAL_MS = 250

export function useServerLogs(identity: () => string | null)
{
  const { t } = useI18n()
  const entries = shallowRef<ServerLogEntry[]>([])
  const streamKey = ref(0)
  const resetReason = ref<ServerLogResetReason | null>(null)
  const loading = ref(false)
  const retrying = ref(false)
  const failure = shallowRef<unknown>(null)
  const accessDenied = ref(false)
  const error = computed(() =>
  {
    if (accessDenied.value)
    {
      return t('serverLogs.accessDenied')
    }
    const value = failure.value
    if (value === null)
    {
      return ''
    }
    return value instanceof Error && value.message ? value.message : t('transcoding.logLoadFailed')
  })
  let cursor: string | undefined
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined

  function stop(): void
  {
    clearTimeout(timer)
    timer = undefined
    controller?.abort()
    controller = undefined
  }

  async function poll(): Promise<void>
  {
    const request = new AbortController()
    controller = request
    let nextDelay: number | null = POLL_INTERVAL_MS
    try
    {
      const page = await ServerLogsAPI.read(cursor, request.signal)
      if (request.signal.aborted)
      {
        return
      }
      cursor = page.nextCursor
      if (page.resetReason)
      {
        resetReason.value = page.resetReason
        streamKey.value++
        entries.value = page.entries
      }
      else if (page.entries.length)
      {
        entries.value = [...entries.value, ...page.entries].slice(-MAX_SERVER_LOG_ENTRIES)
      }
      failure.value = null
      retrying.value = false
      nextDelay = page.hasMore ? CATCH_UP_INTERVAL_MS : POLL_INTERVAL_MS
    }
    catch (value)
    {
      if (request.signal.aborted)
      {
        return
      }
      accessDenied.value = value instanceof ApiError && (value.status === 401 || value.status === 403)
      if (accessDenied.value)
      {
        entries.value = []
        resetReason.value = null
        nextDelay = null
      }
      failure.value = value ?? undefined
      retrying.value = !accessDenied.value
    }
    finally
    {
      if (!request.signal.aborted)
      {
        controller = undefined
        loading.value = false
        if (nextDelay !== null)
        {
          timer = setTimeout(() => { void poll() }, nextDelay)
        }
      }
    }
  }

  watch(identity, (userId) =>
  {
    stop()
    cursor = undefined
    streamKey.value++
    entries.value = []
    resetReason.value = null
    accessDenied.value = false
    failure.value = null
    retrying.value = false
    loading.value = userId !== null
    if (userId !== null)
    {
      void poll()
    }
  }, { immediate: true, flush: 'sync' })
  onScopeDispose(stop)

  return { entries, streamKey, resetReason, loading, retrying, error }
}
