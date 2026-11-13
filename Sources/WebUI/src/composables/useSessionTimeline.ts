import { onBeforeUnmount, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import type { SessionTimeline } from '@/models/sessionTimeline'

const TIMELINE_BUCKET_COUNT = 300

export function useSessionTimeline(sessionId: MaybeRefOrGetter<string>, enabled: MaybeRefOrGetter<boolean>, isLive: MaybeRefOrGetter<boolean>)
{
  const { t } = useI18n()
  const timeline = shallowRef<SessionTimeline>()
  const loading = ref(false)
  const error = ref('')
  let controller: AbortController | undefined
  let generation = 0
  let disposed = false

  async function load(force = false): Promise<void>
  {
    if (disposed || !toValue(enabled) || (!force && timeline.value && !toValue(isLive)))
    {
      return
    }
    const operation = ++generation
    controller?.abort()
    controller = new AbortController()
    const signal = controller.signal
    loading.value = true
    error.value = ''
    try
    {
      const result = await MediaAPI.timeline(toValue(sessionId), TIMELINE_BUCKET_COUNT, signal)
      if (disposed || signal.aborted || operation !== generation)
      {
        return
      }
      if (timeline.value?.revision !== result.revision || timeline.value.durationMs !== result.durationMs || timeline.value.bucketWidthMs !== result.bucketWidthMs)
      {
        timeline.value = result
      }
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && operation === generation)
      {
        error.value = value instanceof Error ? value.message : t('components.player.timelineLoadFailed')
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

  watch([() => toValue(sessionId), () => toValue(enabled)], () =>
  {
    generation++
    controller?.abort()
    timeline.value = undefined
    loading.value = false
    error.value = ''
    void load(true)
  })
  watch(() => toValue(isLive), () => { void load(true) })
  usePolling(() => load(), 10000)
  onBeforeUnmount(() =>
  {
    disposed = true
    generation++
    controller?.abort()
  })
  return { timeline, loading, error, refresh: () => load(true) }
}
