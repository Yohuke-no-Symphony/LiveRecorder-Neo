// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, reactive } from 'vue'
import { MediaAPI } from '../src/apis/MediaAPI'
import SessionTranslationPanel from '../src/components/player/SessionTranslationPanel.vue'
import SessionPlaybackSidebar from '../src/components/player/SessionPlaybackSidebar.vue'
import type { RecordedEvent } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import type { SessionEventSnapshot } from '../src/models/sessionEventSnapshot'

const auth = reactive({ user: { id: 'user-1' }, canConfigureRecordings: false, canViewRecordings: true })
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => auth }))
const mounted: VueWrapper[] = []
const container = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const button = defineComponent({
  props: ['disabled', 'loading'],
  setup(props, { slots }) { return () => h('button', { disabled: props.disabled || props.loading }, slots.default?.()) },
})
const scrollToIndex = vi.fn()
const virtualScroll = defineComponent({
  props: ['items'],
  setup(props, { slots, expose })
  {
    expose({ scrollToIndex })
    return () => h('div', (props.items as RecordedEvent[]).flatMap(item => slots.default?.({ item }) ?? []))
  },
})
const stubs = { VCard: container, VBtn: button, VAlert: container, VVirtualScroll: virtualScroll, VIcon: true, VProgressLinear: true }

function event(sequence: number, offsetMs = sequence * 1000, text = `译者【同传 ${sequence}】后缀`): RecordedEvent
{
  return { sequence, offsetMs, text, userName: '译者', userId: 'translator', schemaVersion: 1, eventId: String(sequence), kind: 'Danmu', command: 'DANMU_MSG', quantity: 1, receivedAtUtc: '2026-11-13T00:00:00Z', payload: { largeProtocolData: 'payload' } }
}

function snapshot(items: RecordedEvent[]): SessionEventSnapshot
{
  return { version: 'v1', reset: true, items, translationSequences: items.map(item => item.sequence), cursor: 'cursor-1', isLive: false }
}

beforeEach(() =>
{
  auth.user = { id: 'user-1' }
  auth.canConfigureRecordings = false
  auth.canViewRecordings = true
  scrollToIndex.mockReset()
  i18n.global.locale.value = 'zh-CN'
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.useRealTimers()
  i18n.global.locale.value = 'zh-CN'
})

describe('同传列表和播放侧栏', () =>
{
  it('同传匹配失败单独提示，点击刷新会完整重建索引', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValueOnce({ ...snapshot([event(1)]), translationSequences: [], translationError: '同传匹配超时' }).mockResolvedValue(snapshot([event(1)]))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', durationMs: 180000, enabled: true, isLive: false }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('同传匹配超时')
    await wrapper.get('[aria-label="刷新同传"]').trigger('click')
    await flushPromises()
    expect(read.mock.calls[1]?.[1]).toBeUndefined()
    expect(wrapper.text()).not.toContain('同传匹配超时')
    expect(wrapper.find('.translation-entry').exists()).toBe(true)
  })

  it('只有点击定位按钮才居中最近条目，定位不会跳转视频或重新请求', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValue(snapshot([event(1, 1000), event(2, 4000), event(3, 8000)]))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', offsetMs: 6500, durationMs: 180000, enabled: true, isLive: false }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(scrollToIndex).not.toHaveBeenCalled()
    await wrapper.get('[aria-label="定位当前播放位置"]').trigger('click')
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(2, 'center')
    expect(wrapper.get('.translation-entry-located').text()).toContain('同传 3')
    expect(wrapper.emitted('seek')).toBeUndefined()
    await wrapper.setProps({ offsetMs: 1000, active: false })
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(scrollToIndex).toHaveBeenCalledTimes(1)
    expect(read).toHaveBeenCalledTimes(1)
    await wrapper.get('[aria-label="定位当前播放位置"]').trigger('click')
    expect(scrollToIndex).toHaveBeenLastCalledWith(0, 'center')
  })

  it('隐藏时修改同传规则会失效缓存，恢复时只重载一次', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValue(snapshot([event(1)]))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', durationMs: 180000, enabled: true, isLive: false, configurationRevision: 1 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ configurationRevision: 2 })
    expect(read).toHaveBeenCalledTimes(1)
    read.mockResolvedValue(snapshot([event(2)]))
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('同传 2')
  })

  it('仅配置授权者显示录像设置入口，版本变化后重查共享规则', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValue(snapshot([]))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', durationMs: 180000, enabled: true, isLive: false, configurationRevision: 1 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.find('[aria-label="录像配置"]').exists()).toBe(false)
    auth.canConfigureRecordings = true
    await nextTick()
    await wrapper.get('[aria-label="录像配置"]').trigger('click')
    expect(wrapper.emitted('configure')).toEqual([[]])
    await wrapper.setProps({ configurationRevision: 2 })
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(2)
  })

  it('显示全文与发送者，点击整行按毫秒发送跳转且不解释 HTML', async () =>
  {
    const text = '译者【<script>alert(1)</script>】后缀'
    vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValue(snapshot([event(1, 12345, text)]))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', durationMs: 180000, enabled: true, isLive: false }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain(text)
    expect(wrapper.text()).toContain('译者')
    expect(wrapper.find('script').exists()).toBe(false)
    await wrapper.get('.translation-entry').trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[12345]])
    expect(wrapper.get('.translation-entry').attributes('type')).toBe('button')
    for (const [locale, title] of [['en-US', 'Translations'], ['ja-JP', '同時通訳'], ['zh-CN', '同传']] as const)
    {
      i18n.global.locale.value = locale
      await nextTick()
      expect(wrapper.text()).toContain(title)
    }
  })

  it('加载失败不显示成功空状态，未录制弹幕明确提示', async () =>
  {
    vi.spyOn(MediaAPI, 'eventSnapshot').mockRejectedValue(new Error('请求失败'))
    const wrapper = mount(SessionTranslationPanel, { props: { sessionId: 'session-1', durationMs: 180000, enabled: true, isLive: false }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('请求失败')
    expect(wrapper.text()).not.toContain('没有匹配的同传弹幕')
    await wrapper.setProps({ enabled: false })
    expect(wrapper.text()).toContain('此场录播未保存弹幕')
  })

  it('侧栏提供同传 Tab，激活面板并向播放器转发跳转', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'eventSnapshot').mockResolvedValue(snapshot([event(1, 5000)]))
    const wrapper = mount(SessionPlaybackSidebar, { props: { sessionId: 'session-1', offsetMs: 0, durationMs: 180000, hasDanmu: true, isLive: false, navigationRevision: 0 }, global: { plugins: [i18n], stubs: { ...stubs, VTabs: { name: 'VTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div><slot /></div>' }, VTab: container, EventFeed: true, SessionLiveMarkerPanel: true } } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('同传')
    expect(read).toHaveBeenCalledTimes(1)
    wrapper.getComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'translations')
    await flushPromises()
    const panel = wrapper.getComponent(SessionTranslationPanel)
    expect(panel.props('active')).toBe(true)
    await panel.get('.translation-entry').trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[5000]])
  })
})
