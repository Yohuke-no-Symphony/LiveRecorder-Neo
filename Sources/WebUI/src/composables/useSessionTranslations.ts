import { onBeforeUnmount, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import { MediaAPI } from '@/apis/MediaAPI'
import type { TranslationDanmu } from '@/models/translationDanmu'
import { useAuthStore } from '@/stores/auth'
import { usePolling } from './usePolling'

export function useSessionTranslations(sessionId: MaybeRefOrGetter<string>, durationMs: MaybeRefOrGetter<number>, active: MaybeRefOrGetter<boolean>, enabled: MaybeRefOrGetter<boolean>, isLive: MaybeRefOrGetter<boolean>)
{
  const { t } = useI18n()
  const auth = useAuthStore()
  const entries = shallowRef<TranslationDanmu[]>([])
  const loading = ref(false)
  const hasSnapshot = ref(false)
  const error = ref('')
  let generation = 0
  let controller: AbortController | undefined
  let disposed = false

  function cancel(): void
  {
    generation++
    controller?.abort()
    controller = undefined
    loading.value = false
  }

  async function refresh(): Promise<void>
  {
    if (disposed || !toValue(active) || !toValue(enabled) || !auth.user)
    {
      return
    }
    cancel()
    const operation = generation
    const id = toValue(sessionId)
    const toMs = toValue(durationMs)
    controller = new AbortController()
    const signal = controller.signal
    loading.value = true
    error.value = ''
    try
    {
      const collected = new Map<number, TranslationDanmu>()
      let cursor: number | undefined
      do
      {
        const page = await MediaAPI.events(id, { fromMs: 0, toMs, translationsOnly: true, limit: 1000, cursor }, signal)
        if (disposed || signal.aborted || operation !== generation)
        {
          return
        }
        for (const item of page.items)
        {
          collected.set(item.sequence, { sequence: item.sequence, offsetMs: item.offsetMs, userName: item.userName, text: item.text })
        }
        const next = page.nextCursor ?? undefined
        if (next !== undefined && (!Number.isSafeInteger(next) || next <= (cursor ?? 0)))
        {
          throw new Error(t('translations.loadFailed'))
        }
        cursor = next
      } while (cursor !== undefined)
      const result = [...collected.values()].sort((left, right) => left.offsetMs - right.offsetMs || left.sequence - right.sequence)
      if (result.length !== entries.value.length || result.some((item, index) =>
      {
        const previous = entries.value[index]!
        return item.sequence !== previous.sequence || item.offsetMs !== previous.offsetMs || item.userName !== previous.userName || item.text !== previous.text
      }))
      {
        entries.value = result
      }
      hasSnapshot.value = true
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && operation === generation)
      {
        error.value = value instanceof Error ? value.message : t('translations.loadFailed')
        if (value instanceof ApiError && [401, 403, 404].includes(value.status))
        {
          entries.value = []
          hasSnapshot.value = false
        }
      }
    }
    finally
    {
      if (!disposed && operation === generation)
      {
        loading.value = false
      }
    }
  }

  watch([() => toValue(sessionId), () => toValue(enabled), () => auth.user?.id], () =>
  {
    cancel()
    entries.value = []
    hasSnapshot.value = false
    error.value = ''
    void refresh()
  }, { immediate: true, flush: 'sync' })
  watch(() => toValue(active), value =>
  {
    if (value)
    {
      void refresh()
    }
    else
    {
      cancel()
    }
  })
  watch(() => toValue(isLive), (value, previous) =>
  {
    if (previous && !value)
    {
      void refresh()
    }
  })
  usePolling(async () =>
  {
    if (toValue(isLive) && !loading.value)
    {
      await refresh()
    }
  }, 10000, false)
  onBeforeUnmount(() =>
  {
    disposed = true
    cancel()
  })

  return { entries, loading, hasSnapshot, error, refresh }
}
