import { computed, onScopeDispose, ref, watch } from 'vue'

const RETRY_DELAYS_MS = [2000, 5000, 15000]

export function useRetryableImage(source: () => string | undefined)
{
  const attempt = ref(0)
  let timer: ReturnType<typeof setTimeout> | undefined
  const imageSource = computed(() =>
  {
    const url = source()
    return url && attempt.value > 0 ? `${url}${url.includes('?') ? '&' : '?'}coverRetry=${attempt.value}` : url
  })

  function stop(): void
  {
    clearTimeout(timer)
    timer = undefined
  }

  function retry(): void
  {
    if (!source() || timer !== undefined || attempt.value >= RETRY_DELAYS_MS.length) { return }
    timer = setTimeout(() =>
    {
      timer = undefined
      attempt.value++
    }, RETRY_DELAYS_MS[attempt.value])
  }

  watch(source, () => { stop(); attempt.value = 0 }, { flush: 'sync' })
  onScopeDispose(stop)
  return { imageSource, retry, loaded: stop }
}
