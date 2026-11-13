// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, reactive, ref } from 'vue'
import { MediaAPI } from '../src/apis/MediaAPI'
import { useSessionTranslations } from '../src/composables/useSessionTranslations'
import SessionTranslationPanel from '../src/components/player/SessionTranslationPanel.vue'
import SessionPlaybackSidebar from '../src/components/player/SessionPlaybackSidebar.vue'
import type { EventPage, RecordedEvent } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

const auth = reactive({ user: { id: 'user-1', translationDanmuPattern: null as string | null } })
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => auth }))
const mounted: VueWrapper[] = []
const container = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const button = defineComponent({
  props: ['disabled', 'loading'],
  setup(props, { slots }) { return () => h('button', { disabled: props.disabled || props.loading }, slots.default?.()) },
})
const virtualScroll = defineComponent({
  props: ['items'],
  setup(props, { slots }) { return () => h('div', (props.items as RecordedEvent[]).flatMap(item => slots.default?.({ item }) ?? [])) },
})
const stubs = { VCard: container, VBtn: button, VAlert: container, VVirtualScroll: virtualScroll, VIcon: true, VProgressLinear: true }

function event(sequence: number, offsetMs = sequence * 1000, text = `译者【同传 ${sequence}】后缀`): RecordedEvent
{
  return { sequence, offsetMs, text, userName: '译者', userId: 'translator', schemaVersion: 1, eventId: String(sequence), kind: 'Danmu', command: 'DANMU_MSG', quantity: 1, receivedAtUtc: '2026-11-13T00:00:00Z', payload: { largeProtocolData: 'payload' } }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (value: unknown) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}

function setupFeed(options: { active?: boolean; enabled?: boolean; live?: boolean } = {})
{
  const id = ref('session-1')
  const active = ref(options.active ?? true)
  const enabled = ref(options.enabled ?? true)
  const live = ref(options.live ?? false)
  const duration = ref(180000)
  let feed!: ReturnType<typeof useSessionTranslations>
  const wrapper = mount(defineComponent({ setup()
  {
    feed = useSessionTranslations(id, duration, active, enabled, live)
    return () => h('div')
  } }), { global: { plugins: [i18n] } })
  mounted.push(wrapper)
  return { id, active, enabled, live, duration, wrapper, get feed() { return feed } }
}

beforeEach(() =>
{
  auth.user = { id: 'user-1', translationDanmuPattern: null }
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

describe('全场同传查询', () =>
{
  it('向既有弹幕接口发送同传选项和取消信号', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ items: [], nextCursor: null }), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    const controller = new AbortController()
    await MediaAPI.events('session /中文', { fromMs: 0, toMs: 180000, translationsOnly: true, cursor: 123, limit: 1000 }, controller.signal)
    const [url, options] = fetcher.mock.calls[0]!
    expect(url).toContain('/api/sessions/session%20%2F%E4%B8%AD%E6%96%87/events?')
    expect(new URL(url, 'http://localhost').searchParams.get('translationsOnly')).toBe('true')
    expect(options).toMatchObject({ signal: controller.signal, credentials: 'include' })
  })

  it('遍历全部分页后按时间排序，保留重复正文并丢弃协议负载', async () =>
  {
    const page = deferred<EventPage>()
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValueOnce({ items: [event(1, 120000)], nextCursor: 1 }).mockReturnValueOnce(page.promise)
    const context = setupFeed()
    await flushPromises()
    expect(context.feed.entries.value).toEqual([])
    context.duration.value = 240000
    page.resolve({ items: [event(2, 1000, '【重复】'), event(3, 1000, '【重复】')], nextCursor: null })
    await flushPromises()
    expect(read.mock.calls.map(([, options]) => options)).toEqual([
      { fromMs: 0, toMs: 180000, translationsOnly: true, limit: 1000, cursor: undefined },
      { fromMs: 0, toMs: 180000, translationsOnly: true, limit: 1000, cursor: 1 },
    ])
    expect(read.mock.calls[0]![2]).toBe(read.mock.calls[1]![2])
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([2, 3, 1])
    expect(context.feed.entries.value[0]).toEqual({ sequence: 2, offsetMs: 1000, userName: '译者', text: '【重复】' })
    expect(context.feed.hasSnapshot.value).toBe(true)
  })

  it('非活动或没有录制弹幕时不查询，激活后才读取', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValue({ items: [] })
    const context = setupFeed({ active: false, enabled: false })
    await flushPromises()
    context.active.value = true
    await flushPromises()
    expect(read).not.toHaveBeenCalled()
    context.enabled.value = true
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(1)
    expect(context.feed.hasSnapshot.value).toBe(true)
    await vi.advanceTimersByTimeAsync(30000)
    expect(read).toHaveBeenCalledTimes(1)
  })

  it('切出时取消请求，切回后迟到响应不能覆盖新列表', async () =>
  {
    const old = deferred<EventPage>()
    const read = vi.spyOn(MediaAPI, 'events').mockReturnValueOnce(old.promise).mockResolvedValue({ items: [event(2)] })
    const context = setupFeed()
    context.active.value = false
    await nextTick()
    expect(read.mock.calls[0]![2]?.aborted).toBe(true)
    context.active.value = true
    await flushPromises()
    old.resolve({ items: [event(1)] })
    await flushPromises()
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([2])
    expect(context.feed.loading.value).toBe(false)
  })

  it.each(['session', 'account', 'pattern'] as const)('变更 %s 时清空旧列表且阻止旧请求回写', async scope =>
  {
    const old = deferred<EventPage>()
    const next = deferred<EventPage>()
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValueOnce({ items: [event(1)] }).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise)
    const context = setupFeed()
    await flushPromises()
    void context.feed.refresh()
    if (scope === 'session') { context.id.value = 'session-2' }
    else if (scope === 'account') { auth.user.id = 'user-2' }
    else { auth.user.translationDanmuPattern = '^EN:' }
    await nextTick()
    expect(context.feed.entries.value).toEqual([])
    expect(context.feed.hasSnapshot.value).toBe(false)
    expect(read.mock.calls[1]![2]?.aborted).toBe(true)
    old.resolve({ items: [event(2)] })
    next.resolve({ items: [event(3)] })
    await flushPromises()
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([3])
  })

  it('后续页失败时保留上次完整列表，并允许刷新重试', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValueOnce({ items: [event(1)] })
    const context = setupFeed()
    await flushPromises()
    const original = context.feed.entries.value
    read.mockResolvedValueOnce({ items: [event(2)], nextCursor: 2 }).mockRejectedValueOnce(new Error('匹配超时'))
    await context.feed.refresh()
    expect(context.feed.entries.value).toBe(original)
    expect(context.feed.error.value).toBe('匹配超时')
    read.mockResolvedValueOnce({ items: [event(3)] })
    await context.feed.refresh()
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([3])
    expect(context.feed.error.value).toBe('')
  })

  it('空页继续跟随游标，重复游标报错且不发布残缺列表', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValueOnce({ items: [], nextCursor: 1 }).mockResolvedValueOnce({ items: [event(2)], nextCursor: 1 })
    const context = setupFeed()
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(2)
    expect(context.feed.error.value).not.toBe('')
    expect(context.feed.hasSnapshot.value).toBe(false)
    expect(context.feed.entries.value).toEqual([])
  })

  it('自动刷新不会打断超过轮询周期的加载，直播结束后读取最终结果', async () =>
  {
    const initial = deferred<EventPage>()
    const read = vi.spyOn(MediaAPI, 'events').mockReturnValueOnce(initial.promise).mockResolvedValue({ items: [event(2)] })
    const context = setupFeed({ live: true })
    await vi.advanceTimersByTimeAsync(30000)
    expect(read).toHaveBeenCalledTimes(1)
    expect(read.mock.calls[0]![2]?.aborted).toBe(false)
    context.duration.value = 240000
    context.live.value = false
    await flushPromises()
    expect(read.mock.calls[0]![2]?.aborted).toBe(true)
    expect(read.mock.calls[1]![1].toMs).toBe(240000)
    initial.resolve({ items: [event(1)] })
    await flushPromises()
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([2])
    await vi.advanceTimersByTimeAsync(30000)
    expect(read).toHaveBeenCalledTimes(2)
  })

  it('直播重扫包含先前被时长裁剪的低序号事件，内容未变时保持列表引用', async () =>
  {
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValueOnce({ items: [event(2, 1000)] }).mockResolvedValue({ items: [event(1, 190000), event(2, 1000)] })
    const context = setupFeed({ live: true })
    await flushPromises()
    context.duration.value = 240000
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(read.mock.calls[1]![1]).toMatchObject({ fromMs: 0, toMs: 240000, cursor: undefined })
    expect(context.feed.entries.value.map(item => item.sequence)).toEqual([2, 1])
    const previous = context.feed.entries.value
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(context.feed.entries.value).toBe(previous)
    context.wrapper.unmount()
    await vi.advanceTimersByTimeAsync(20000)
    expect(read).toHaveBeenCalledTimes(3)
  })
})

describe('同传列表和播放侧栏', () =>
{
  it('显示全文与发送者，点击整行按毫秒发送跳转且不解释 HTML', async () =>
  {
    const text = '译者【<script>alert(1)</script>】后缀'
    vi.spyOn(MediaAPI, 'events').mockResolvedValue({ items: [event(1, 12345, text)] })
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
    vi.spyOn(MediaAPI, 'events').mockRejectedValue(new Error('请求失败'))
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
    const read = vi.spyOn(MediaAPI, 'events').mockResolvedValue({ items: [event(1, 5000)] })
    const wrapper = mount(SessionPlaybackSidebar, { props: { sessionId: 'session-1', offsetMs: 0, durationMs: 180000, hasDanmu: true, isLive: false, navigationRevision: 0 }, global: { plugins: [i18n], stubs: { ...stubs, VTabs: { name: 'VTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div><slot /></div>' }, VTab: container, EventFeed: true, SessionLiveMarkerPanel: true } } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('同传')
    expect(read).not.toHaveBeenCalled()
    wrapper.getComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'translations')
    await flushPromises()
    const panel = wrapper.getComponent(SessionTranslationPanel)
    expect(panel.props('active')).toBe(true)
    await panel.get('.translation-entry').trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[5000]])
  })
})
