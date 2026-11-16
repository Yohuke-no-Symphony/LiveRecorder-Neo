import { inject, onBeforeUnmount, provide, ref, shallowRef, toValue, watch, type InjectionKey, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import { MediaAPI } from '@/apis/MediaAPI'
import type { CachedSessionEvents } from '@/models/sessionEventSnapshot'
import type { RecordedEvent } from '@/models/media'
import type { TranslationDanmu } from '@/models/translationDanmu'
import { useAuthStore } from '@/stores/auth'
import { previewCacheKey, readPreviewCache, removePreviewCache, writePreviewCache } from '@/utilities/previewBrowserCache'
import { mergePlaybackEntries } from '@/utilities/mergePlaybackEntries'
import { usePolling } from './usePolling'

interface SessionEventSnapshotOptions
{
  sessionId: MaybeRefOrGetter<string>
  active: MaybeRefOrGetter<boolean>
  enabled: MaybeRefOrGetter<boolean>
  isLive: MaybeRefOrGetter<boolean>
  configurationRevision?: MaybeRefOrGetter<number | undefined>
}

const SNAPSHOT_FORMAT = 'events-v1'
const LIVE_PERSIST_INTERVAL_MS = 60000
const snapshotKey: InjectionKey<ReturnType<typeof createSessionEventSnapshot>> = Symbol('session-event-snapshot')

export function provideSessionEventSnapshot(options: SessionEventSnapshotOptions)
{
  const state = createSessionEventSnapshot(options)
  provide(snapshotKey, state)
  return state
}

export function useSessionEventSnapshot(options: SessionEventSnapshotOptions)
{
  return inject(snapshotKey, undefined) ?? createSessionEventSnapshot(options)
}

function createSessionEventSnapshot(options: SessionEventSnapshotOptions)
{
  const { t, locale } = useI18n()
  const auth = useAuthStore()
  const events = shallowRef<RecordedEvent[]>([])
  const translations = shallowRef<TranslationDanmu[]>([])
  const loading = ref(false)
  const hasSnapshot = ref(false)
  const resetRevision = ref(0)
  const error = ref('')
  const translationError = ref('')
  let snapshot: CachedSessionEvents | undefined
  let controller: AbortController | undefined
  let generation = 0
  let disposed = false
  let dirty = true
  let lastPersistAt = -Infinity
  let knownSequences = new Set<number>()
  let forceReload = false

  function identity(): string
  {
    return JSON.stringify([auth.user?.id, auth.user?.role, auth.user?.status, [...(auth.user?.permissions ?? [])].sort()])
  }

  function allowed(): boolean
  {
    return !disposed && !!auth.user && auth.canViewRecordings && toValue(options.enabled) && toValue(options.active)
  }

  function cancel(): void
  {
    generation++
    controller?.abort()
    controller = undefined
    loading.value = false
  }

  function clear(): void
  {
    snapshot = undefined
    events.value = []
    translations.value = []
    translationError.value = ''
    knownSequences = new Set()
    hasSnapshot.value = false
    resetRevision.value++
  }

  async function refresh(force = false): Promise<void>
  {
    dirty = true
    forceReload ||= force
    if (!allowed() || loading.value)
    {
      return
    }
    dirty = false
    const completeRead = forceReload
    forceReload = false
    const operation = generation
    const id = toValue(options.sessionId)
    const key = previewCacheKey('events', id, identity(), SNAPSHOT_FORMAT)
    const request = new AbortController()
    controller = request
    const signal = request.signal
    loading.value = true
    error.value = ''
    try
    {
      const stored = completeRead ? undefined : snapshot ?? await readPreviewCache<CachedSessionEvents>(key)
      if (disposed || signal.aborted || operation !== generation)
      {
        return
      }
      const previous = stored && typeof stored.version === 'string' && typeof stored.cursor === 'string' && Array.isArray(stored.items) && Array.isArray(stored.translationSequences) ? stored : undefined
      const response = await MediaAPI.eventSnapshot(id, previous?.cursor, signal)
      if (disposed || signal.aborted || operation !== generation)
      {
        return
      }
      if (!response.version || !response.cursor || !Array.isArray(response.items) || !Array.isArray(response.translationSequences) || (!response.reset && (!previous || previous.version !== response.version)))
      {
        throw new Error(t('components.events.loadFailed'))
      }
      const base = response.reset ? undefined : previous
      if (response.reset)
      {
        resetRevision.value++
      }
      if (!base || base !== snapshot)
      {
        knownSequences = new Set(base?.items.map(item => item.sequence))
      }
      const additions = response.items.filter(item =>
      {
        if (knownSequences.has(item.sequence))
        {
          return false
        }
        knownSequences.add(item.sequence)
        return true
      })
      const items = mergePlaybackEntries(base?.items ?? [], additions)
      const sequences = [...new Set([...(base?.translationSequences ?? []), ...response.translationSequences])]
      const indexError = response.translationError ?? base?.translationError ?? ''
      snapshot = { version: response.version, cursor: response.cursor, items, translationSequences: indexError ? [] : sequences, translationError: indexError }
      const restoring = !hasSnapshot.value || response.reset
      events.value = items
      translationError.value = indexError
      if (indexError)
      {
        translations.value = []
      }
      else if (restoring)
      {
        const selected = new Set(sequences)
        translations.value = items.filter(item => selected.has(item.sequence)).map(item => ({ sequence: item.sequence, offsetMs: item.offsetMs, userName: item.userName, text: item.text }))
      }
      else if (additions.length)
      {
        const selected = new Set(response.translationSequences)
        const translated = additions.filter(item => selected.has(item.sequence)).map(item => ({ sequence: item.sequence, offsetMs: item.offsetMs, userName: item.userName, text: item.text }))
        translations.value = mergePlaybackEntries(translations.value, translated)
      }
      hasSnapshot.value = true
      const now = Date.now()
      if (!toValue(options.isLive) || !previous || now - lastPersistAt >= LIVE_PERSIST_INTERVAL_MS)
      {
        lastPersistAt = now
        await writePreviewCache(key, snapshot)
      }
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && operation === generation)
      {
        error.value = value instanceof Error ? value.message : t('components.events.loadFailed')
        if (value instanceof ApiError && [401, 403, 404, 410].includes(value.status))
        {
          clear()
          await removePreviewCache(key)
        }
      }
    }
    finally
    {
      if (!disposed && operation === generation)
      {
        controller = undefined
        loading.value = false
        if (dirty && allowed())
        {
          void refresh()
        }
      }
    }
  }

  watch([() => toValue(options.sessionId), () => toValue(options.enabled), () => auth.canViewRecordings, identity, locale, () => toValue(options.configurationRevision)], () =>
  {
    cancel()
    clear()
    dirty = true
    error.value = ''
    lastPersistAt = -Infinity
    forceReload = false
    void refresh()
  }, { immediate: true, flush: 'sync' })
  watch(() => toValue(options.active), value =>
  {
    if (value && !loading.value && (!hasSnapshot.value || dirty || error.value || toValue(options.isLive)))
    {
      void refresh()
    }
  })
  watch(() => toValue(options.isLive), (value, previous) =>
  {
    if (previous && !value)
    {
      void refresh()
    }
  })
  usePolling(async () =>
  {
    if (allowed() && toValue(options.isLive) && !loading.value)
    {
      await refresh()
    }
  }, 10000, false)
  onBeforeUnmount(() =>
  {
    disposed = true
    cancel()
  })
  return { events, translations, loading, hasSnapshot, resetRevision, error, translationError, refresh }
}
