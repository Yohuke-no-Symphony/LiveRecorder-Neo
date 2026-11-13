// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { i18n } from '../src/plugins/i18n'
import type { PlaybackWindow } from '../src/models/media'
import MediaPlayer from '../src/components/player/MediaPlayer.vue'
import PlaybackTimeline from '../src/components/player/PlaybackTimeline.vue'

vi.mock('hls.js', () => ({ default: Object.assign(vi.fn(), { isSupported: () => false }) }))
const mounted: VueWrapper[] = []
let tick: globalThis.FrameRequestCallback
function mountPlayer()
{
  const loadWindow = vi.fn(async (offsetMs: number): Promise<PlaybackWindow> => ({ url: null, startOffsetMs: offsetMs, endOffsetMs: 10000, requestedOffsetMs: offsetMs, isGap: true, gapEndMs: offsetMs < 5000 ? 5000 : 10000, durationMs: 10000 }))
  const wrapper = mount(MediaPlayer, { props: { modelValue: 0, durationMs: 10000, gaps: [], isLive: false, loadWindow }, global: { plugins: [i18n], stubs: { VBtn: true, VSlider: true, VMenu: true, VList: true, VListItem: true, VIcon: true, VSpacer: true, VProgressCircular: true } } })
  mounted.push(wrapper)
  return { wrapper, loadWindow }
}
beforeEach(() =>
{
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.stubGlobal('requestAnimationFrame', vi.fn((callback: globalThis.FrameRequestCallback) => { tick = callback; return 1 }))
  vi.stubGlobal('cancelAnimationFrame', vi.fn())
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers() })

describe('用户导航与自动续播', () =>
{
  it('点击记录会取消先前键盘进度条的延迟跳转', async () =>
  {
    const { wrapper, loadWindow } = mountPlayer()
    await flushPromises()
    expect(wrapper.emitted('navigate')).toBeUndefined()
    const timeline = wrapper.findComponent(PlaybackTimeline)
    timeline.vm.$emit('update:modelValue', 7000)
    await wrapper.vm.$nextTick()
    timeline.vm.$emit('seek', 6000)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(200)
    await flushPromises()
    expect(wrapper.emitted('navigate')).toHaveLength(1)
    expect(loadWindow.mock.calls.at(-1)?.[0]).toBe(6000)
  })
  it('开始拖动会取消此前的键盘延迟定位', async () =>
  {
    const { wrapper, loadWindow } = mountPlayer()
    await flushPromises()
    const timeline = wrapper.findComponent(PlaybackTimeline)
    timeline.vm.$emit('update:modelValue', 7000)
    await wrapper.vm.$nextTick()
    timeline.vm.$emit('start')
    await vi.advanceTimersByTimeAsync(200)
    expect(wrapper.emitted('navigate')).toBeUndefined()
    expect(loadWindow).toHaveBeenCalledTimes(1)
    timeline.vm.$emit('update:modelValue', 8000)
    timeline.vm.$emit('end')
    await flushPromises()
    expect(wrapper.emitted('navigate')).toHaveLength(1)
    expect(loadWindow.mock.calls.at(-1)?.[0]).toBe(8000)
  })
  it('自动越过断流窗口不会发送用户导航事件', async () =>
  {
    const { wrapper, loadWindow } = mountPlayer()
    await flushPromises()
    await wrapper.get('v-btn-stub[icon="mdi-play"]').trigger('click')
    tick(1000)
    tick(6000)
    await flushPromises()
    expect(loadWindow.mock.calls.at(-1)?.[0]).toBe(5000)
    expect(wrapper.emitted('navigate')).toBeUndefined()
  })
})
