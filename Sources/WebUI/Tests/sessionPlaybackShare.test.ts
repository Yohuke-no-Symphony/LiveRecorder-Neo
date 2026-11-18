// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createPinia, disposePinia, type Pinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { MediaAPI } from '../src/apis/MediaAPI'
import { PERMISSIONS } from '../src/models/auth'
import type { SessionDetail } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import SessionView from '../src/views/SessionView.vue'

const id = 'session /中文'
const sessionPath = `/sessions/${encodeURIComponent(id)}`
const copyLabel = '复制当前时间链接'
const seek = vi.fn<(offsetMs: number) => Promise<void>>().mockResolvedValue()
const player = defineComponent({
  props: ['initialOffsetMs', 'modelValue', 'gaps'],
  emits: ['update:modelValue'],
  setup(_, { expose })
  {
    expose({ seek })
    return () => h('div')
  },
})
const overlay = defineComponent({
  props: ['modelValue'],
  setup(props, { slots }) { return () => props.modelValue ? h('section', slots.default?.()) : null },
})
const field = defineComponent({
  props: { modelValue: String, label: String, readonly: Boolean },
  setup(props) { return () => h('input', { value: props.modelValue, 'aria-label': props.label, readonly: props.readonly }) },
})
const container = { template: '<div><slot /></div>' }
let wrapper: VueWrapper | undefined
let pinia: Pinia | undefined

async function open(query = '')
{
  i18n.global.locale.value = 'zh-CN'
  const detail: SessionDetail = {
    session: {
      id, createdAtUtc: '2026-11-13T00:00:00Z', streamerId: 'streamer-1', roomId: 100,
      title: '测试录播', coverUrl: '', startedAtUtc: '2026-11-13T00:00:00Z', endedAtUtc: '2026-11-13T00:00:10Z',
      lastMediaAtUtc: '2026-11-13T00:00:10Z', state: 'Completed', hasDanmu: false, totalBytes: 2048,
      isTrashed: false, isPermanent: false, mediaRevision: 1,
    },
    segments: [], durationMs: 10000, gaps: [{ startMs: 4000, endMs: 4500 }],
  }
  vi.spyOn(MediaAPI, 'session').mockResolvedValue(detail)
  vi.spyOn(MediaAPI, 'streamers').mockResolvedValue([])
  vi.spyOn(MediaAPI, 'bookmarks').mockResolvedValue([])
  pinia = createPinia()
  useAuthStore(pinia).user = {
    id: 'viewer', email: 'viewer@example.test', displayName: 'Viewer', role: 'User',
    status: 'Active', permissions: [PERMISSIONS.viewRecordings],
  }
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/sessions/:id', component: SessionView }] })
  await router.push(`${sessionPath}${query}`)
  wrapper = mount(SessionView, {
    global: {
      plugins: [i18n, pinia, router],
      stubs: {
        MediaPlayer: player, SessionPlaybackSidebar: true, PlaybackBookmarkActions: true,
        SessionBookmarkList: true, SessionBookmarkDialog: true, StatusChip: true, ConfirmDialog: true,
        VDefaultsProvider: container, VCard: container, VCardText: container,
        VBtn: { template: '<button><slot /></button>' }, VIcon: true, VTextField: field,
        VDialog: overlay, VSnackbar: overlay, VProgressLinear: true,
        VTooltip: { template: '<slot name="activator" :props="{}" />' },
      },
    },
  })
  await flushPromises()
  return { view: wrapper, router }
}

afterEach(() =>
{
  wrapper?.unmount()
  if (pinia) { disposePinia(pinia) }
  wrapper = undefined
  pinia = undefined
  seek.mockClear()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

describe('录播时间点分享', () =>
{
  it('打开场次时传入时间点，切换到同一场次的零时间链接时定位', async () =>
  {
    const { view, router } = await open('?at=4000.25')
    expect(view.getComponent(player).props('initialOffsetMs')).toBe(4000.25)
    expect(view.getComponent(player).props('gaps')).toEqual([{ startMs: 4000, endMs: 4500 }])
    await router.push(`${sessionPath}?at=0`)
    expect(seek).toHaveBeenCalledExactlyOnceWith(0)
    await router.push(`${sessionPath}?at=0&panel=markers`)
    await router.push(`${sessionPath}?at=1&at=2`)
    expect(seek).toHaveBeenCalledTimes(1)
    expect(MediaAPI.session).toHaveBeenCalledExactlyOnceWith(id)
  })

  it.each([
    ['', 0], ['?at=', 0], ['?at=-1', 0], ['?at=Infinity', 0], ['?at=4000ms', 0],
    ['?at=1&at=2', 0], ['?at=10000', 9999], ['?at=20000', 9999],
  ])('使用 %s 打开时的初始偏移为 %d 毫秒', async (query, offset) =>
  {
    const { view } = await open(query)
    expect(view.getComponent(player).props('initialOffsetMs')).toBe(offset)
  })

  it('复制站点地址和点击时的整数毫秒位移', async () =>
  {
    const writeText = vi.fn<(value: string) => Promise<void>>().mockResolvedValue()
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubEnv('VITE_API_BASE_URL', 'https://recordings.example.test')
    const { view, router } = await open('?at=100&panel=markers')
    view.getComponent(player).vm.$emit('update:modelValue', 6042.75)
    await view.get(`button[aria-label="${copyLabel}"]`).trigger('click')
    await flushPromises()
    expect(writeText).toHaveBeenCalledExactlyOnceWith(`${window.location.origin}${sessionPath}?at=6042`)
    expect(router.currentRoute.value.query).toEqual({ at: '100', panel: 'markers' })
    expect(view.text()).toContain('链接已复制')
  })

  it('剪贴板写入失败时显示点击时捕获的可手动复制地址', async () =>
  {
    let rejectCopy!: (reason: Error) => void
    const writeText = vi.fn(() => new Promise<void>((_, reject) => { rejectCopy = reject }))
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    const { view } = await open()
    view.getComponent(player).vm.$emit('update:modelValue', 3200)
    await view.get(`button[aria-label="${copyLabel}"]`).trigger('click')
    view.getComponent(player).vm.$emit('update:modelValue', 4500)
    rejectCopy(new Error('Clipboard denied'))
    await flushPromises()
    const dialog = view.get(`section[aria-label="${copyLabel}"]`)
    const input = dialog.get<HTMLInputElement>('input[aria-label="链接"]').element
    expect(input.value).toBe(`${window.location.origin}${sessionPath}?at=3200`)
    expect(input.readOnly).toBe(true)
    expect(dialog.text()).toContain('请选择链接并手动复制。')
  })
})
