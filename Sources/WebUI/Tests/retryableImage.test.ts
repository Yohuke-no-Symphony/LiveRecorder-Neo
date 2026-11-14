import { effectScope, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useRetryableImage } from '../src/composables/useRetryableImage'

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

describe('cover image retries', () =>
{
  it('retries failures with new URLs and stops after three attempts', async () =>
  {
    const scope = effectScope()
    const model = scope.run(() => useRetryableImage(() => '/api/sessions/one/cover'))!
    try
    {
      for (let attempt = 1; attempt <= 3; attempt++)
      {
        model.retry()
        model.retry()
        expect(vi.getTimerCount()).toBe(1)
        await vi.runOnlyPendingTimersAsync()
        expect(model.imageSource.value).toBe(`/api/sessions/one/cover?coverRetry=${attempt}`)
      }
      model.retry()
      expect(vi.getTimerCount()).toBe(0)
    }
    finally { scope.stop() }
  })

  it('cancels pending retries after a successful load or permission removal', async () =>
  {
    const source = ref<string | undefined>('/api/sessions/one/cover')
    const scope = effectScope()
    const model = scope.run(() => useRetryableImage(() => source.value))!
    try
    {
      model.retry()
      model.loaded()
      expect(vi.getTimerCount()).toBe(0)
      model.retry()
      source.value = undefined
      await vi.runAllTimersAsync()
      expect(model.imageSource.value).toBeUndefined()
      model.retry()
      expect(vi.getTimerCount()).toBe(0)
    }
    finally { scope.stop() }
  })

  it('resets for another session and clears timers on disposal', async () =>
  {
    const source = ref('/api/sessions/one/cover')
    const scope = effectScope()
    const model = scope.run(() => useRetryableImage(() => source.value))!
    model.retry()
    await vi.runOnlyPendingTimersAsync()
    model.retry()
    source.value = '/api/sessions/two/cover?version=2'
    expect(vi.getTimerCount()).toBe(0)
    expect(model.imageSource.value).toBe(source.value)
    model.retry()
    await vi.runOnlyPendingTimersAsync()
    expect(model.imageSource.value).toBe('/api/sessions/two/cover?version=2&coverRetry=1')
    model.retry()
    scope.stop()
    expect(vi.getTimerCount()).toBe(0)
  })
})
