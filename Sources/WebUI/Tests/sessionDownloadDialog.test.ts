// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, ref } from 'vue'
import { createPinia, disposePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import SessionDownloadDialog from '../src/components/player/SessionDownloadDialog.vue'
import SessionView from '../src/views/SessionView.vue'
import CardHeader from '../src/components/common/CardHeader.vue'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import type { RecordingSegment, SessionDetail } from '../src/models/media'
import { useAuthStore } from '../src/stores/auth'
import { PERMISSIONS } from '../src/models/auth'
import * as downloadFile from '../src/utilities/downloadFile'

const container = { template: '<div><slot /></div>' }
const dialog = defineComponent({
  name: 'VDialog', props: ['modelValue', 'scrollable', 'maxWidth'],
  setup(props, { slots }) { return () => props.modelValue ? h('section', slots.default?.()) : null },
})
const button = defineComponent({
  props: ['disabled', 'href', 'type', 'icon'],
  setup(props, { slots })
  {
    return () => h(props.href && !props.disabled ? 'a' : 'button', {
      href: props.disabled ? undefined : props.href,
      disabled: props.disabled, type: props.type ?? 'button',
    }, slots.default?.())
  },
})
const menu = defineComponent({
  setup(_, { slots })
  {
    const opened = ref(false)
    return () => h('div', [
      slots.activator?.({ props: { 'aria-expanded': opened.value, onClick: () => { opened.value = !opened.value } } }),
      opened.value ? h('div', { role: 'menu', onClick: () => { opened.value = false } }, slots.default?.()) : null,
    ])
  },
})
const menuItem = defineComponent({
  props: { title: String, disabled: Boolean },
  setup: props => () => h('button', { disabled: props.disabled }, props.title),
})
const checkbox = defineComponent({
  props: ['modelValue', 'value', 'label', 'disabled', 'indeterminate'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [h('input', {
      type: 'checkbox', disabled: props.disabled, indeterminate: props.indeterminate,
      checked: Array.isArray(props.modelValue) ? props.modelValue.includes(props.value) : props.modelValue,
      onChange: (event: Event) =>
      {
        const selected = (event.target as HTMLInputElement).checked
        emit('update:modelValue', Array.isArray(props.modelValue)
          ? selected ? [...props.modelValue, props.value] : props.modelValue.filter((value: string) => value !== props.value)
          : selected)
      },
    }), props.label])
  },
})
const mounted: VueWrapper[] = []
const receivedDownloads: string[] = []
const stubs = {
  VDialog: dialog, VCard: container, VCardText: container, VCardActions: container,
  VBtn: button, VCheckbox: checkbox, VChip: container, VDivider: true, VIcon: true, VSpacer: true,
  VTooltip: { template: '<slot name="activator" :props="{}" />' },
}

function segment(id: string, changes: Partial<RecordingSegment> = {}): RecordingSegment
{
  return {
    id, createdAtUtc: '2026-11-13T00:00:00Z', sessionId: 'session-1', attemptId: 'attempt-1',
    relativePath: `录像/${id}.flv`, startOffsetMs: 0, durationMs: 10000, committedBytes: 2048,
    isClosed: true, revision: 1, codec: 'avc', ...changes,
  }
}

function open(segments: RecordingSegment[] = [segment('part-1'), segment('part-2', { startOffsetMs: 12000 })]): VueWrapper
{
  const wrapper = mount(SessionDownloadDialog, {
    props: { modelValue: true, sessionId: 'session-1', sessionTitle: '测试录播', segments },
    global: {
      plugins: [i18n],
      stubs,
    },
  })
  mounted.push(wrapper)
  return wrapper
}

function action(wrapper: VueWrapper): ReturnType<VueWrapper['get']>
{
  return wrapper.get('button[data-download-selected]')
}

beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  receivedDownloads.length = 0
  vi.stubEnv('VITE_API_BASE_URL', 'https://recordings.example.test')
  vi.stubGlobal('fetch', vi.fn())
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function(this: HTMLAnchorElement)
  {
    receivedDownloads.push(this.href)
    expect(this.hasAttribute('download')).toBe(true)
  })
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  i18n.global.locale.value = 'zh-CN'
})

describe('录播原始分段下载', () =>
{
  it('播放页通过下载菜单获取两种文本或打开视频分段弹窗', async () =>
  {
    const segments = [segment('part-1')]
    const detail: SessionDetail = {
      session: {
        id: 'session-1', createdAtUtc: '2026-11-13T00:00:00Z', streamerId: 'streamer-1', roomId: 100,
        title: '测试录播', coverUrl: '', startedAtUtc: '2026-11-13T00:00:00Z', endedAtUtc: '2026-11-13T00:00:10Z',
        lastMediaAtUtc: '2026-11-13T00:00:10Z', state: 'Completed', hasDanmu: false,
        totalBytes: 2048, isTrashed: false, isPermanent: false, expiresAtUtc: '2026-11-20T00:00:10Z', mediaRevision: 1,
      },
      segments, durationMs: 10000, gaps: [],
    }
    vi.spyOn(MediaAPI, 'session').mockResolvedValue(detail)
    vi.spyOn(MediaAPI, 'streamers').mockResolvedValue([])
    vi.spyOn(MediaAPI, 'bookmarks').mockResolvedValue([])
    vi.spyOn(MediaAPI, 'timeline').mockResolvedValue({ durationMs: 10000, bucketWidthMs: 100, revision: 'test', heat: [], markers: [] })
    const save = vi.spyOn(downloadFile, 'saveDownloadedFile').mockImplementation(() => {})
    const pinia = createPinia()
    useAuthStore(pinia).user = {
      id: 'viewer', email: 'viewer@example.test', displayName: 'Viewer', role: 'User',
      status: 'Active', permissions: [PERMISSIONS.viewRecordings, PERMISSIONS.downloadRecordings],
    }
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/sessions/:id', component: SessionView }] })
    await router.push('/sessions/session-1')
    const wrapper = mount(SessionView, {
      global: {
        plugins: [i18n, pinia, router],
        stubs: {
          ...stubs, MediaPlayer: true, SessionPlaybackSidebar: true, ClipDialog: true, PlaybackBookmarkActions: true,
          SessionBookmarkList: true, SessionBookmarkDialog: true, ConfirmDialog: true, StatusChip: true,
          SessionGroupDialog: true, SessionSplitDialog: true, VProgressLinear: true, VAlert: true,
          VExpansionPanels: true, VExpansionPanel: true, VExpansionPanelText: true, VTable: true, VSnackbar: true,
          VMenu: menu, VList: container, VListItem: menuItem,
        },
      },
    })
    try
    {
      await flushPromises()
      expect(wrapper.get('.session-meta time').attributes('datetime')).toBe(detail.session.expiresAtUtc)
      expect(wrapper.get('.session-meta time').text()).toContain('到期')
      const download = wrapper.get('button[aria-label="下载"]')
      expect(wrapper.find('[role="menu"]').exists()).toBe(false)
      await download.trigger('click')
      const items = () => wrapper.findAll('[role="menuitem"]')
      expect(items().map(item => item.text())).toEqual(['下载视频', '下载弹幕', '下载同传'])

      for (const [index, path, filename] of [
        [1, '', '测试录播_弹幕.txt'], [2, '?translationsOnly=true', '测试录播_同传.txt'],
      ] as const)
      {
        vi.mocked(fetch).mockResolvedValueOnce(new Response('[00:00:00.500] 观众 · 测试', {
          headers: { 'Content-Type': 'text/plain; charset=utf-8', 'Content-Disposition': `attachment; filename*=UTF-8''${encodeURIComponent(filename)}` },
        }))
        await items()[index]!.trigger('click')
        await flushPromises()
        expect(fetch).toHaveBeenLastCalledWith(`https://recordings.example.test/api/sessions/session-1/events/download${path}`, expect.objectContaining({ credentials: 'include' }))
        expect(save).toHaveBeenLastCalledWith(expect.objectContaining({ fileName: filename }))
        expect(wrapper.getComponent(SessionDownloadDialog).props('modelValue')).toBe(false)
        await download.trigger('click')
      }

      await items()[0]!.trigger('click')
      const dialog = wrapper.getComponent(SessionDownloadDialog)
      expect(dialog.props()).toMatchObject({ modelValue: true, sessionId: 'session-1', sessionTitle: '测试录播', segments })
      expect(dialog.findComponent(CardHeader).props('title')).toBe('下载录播')
      expect(fetch).toHaveBeenCalledTimes(2)

      useAuthStore(pinia).user!.permissions = [PERMISSIONS.viewRecordings]
      await nextTick()
      expect(wrapper.find('button[aria-label="下载"]').exists()).toBe(false)
      expect(wrapper.findComponent(SessionDownloadDialog).exists()).toBe(false)
    }
    finally
    {
      wrapper.unmount()
      disposePinia(pinia)
    }
  })

  it('默认全选有效已完成分段，沿用共用卡片标题并显示分段信息', () =>
  {
    const wrapper = open([
      segment('first'), segment('recording', { isClosed: false }), segment('empty', { durationMs: 0 }),
      segment('header', { committedBytes: 12 }), segment('last', { startOffsetMs: 12000, committedBytes: 4096 }),
    ])
    expect(wrapper.findComponent(CardHeader).props('title')).toBe('下载录播')
    expect(wrapper.findComponent(dialog).props('scrollable')).not.toBe(false)
    const inputs = wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')
    expect(inputs.map(item => item.element.checked)).toEqual([true, true, false, false, false, true])
    expect(inputs.map(item => item.element.disabled)).toEqual([false, false, true, true, true, false])
    expect(wrapper.text()).toContain('测试录播')
    expect(wrapper.text()).toContain('已选 2 个片段')
    expect(wrapper.text()).toContain('6 KB')
    expect(wrapper.text()).toContain('00:00:12')
    expect(wrapper.text()).toContain('00:10')
    expect(wrapper.text()).toContain('录制中，完成后可下载')
  })

  it('全选与取消选择保持勾选和主按钮状态一致', async () =>
  {
    const wrapper = open()
    const inputs = wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')
    await inputs[0]!.setValue(false)
    expect(inputs.every(item => !item.element.checked)).toBe(true)
    expect(action(wrapper).attributes('disabled')).toBeDefined()

    await inputs[1]!.setValue(true)
    expect(inputs[0]!.element.indeterminate).toBe(true)
    expect(action(wrapper).attributes('disabled')).toBeUndefined()
    await inputs[0]!.setValue(true)
    expect(inputs.every(item => item.element.checked)).toBe(true)
  })

  it('仅为已选分段发起原始文件下载，不请求整片 Blob 或触发转码', async () =>
  {
    const wrapper = open()
    await wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')[1]!.setValue(false)
    await action(wrapper).trigger('click')

    expect(receivedDownloads).toEqual(['https://recordings.example.test/api/sessions/session-1/segments/part-2/download'])
    expect(fetch).not.toHaveBeenCalled()
    expect(wrapper.get('[role="status"]').text()).toContain('已请求下载 1 个片段')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('默认下载全片时按原始分段顺序分别发起下载', async () =>
  {
    const wrapper = open()
    await action(wrapper).trigger('click')
    expect(receivedDownloads).toEqual([
      'https://recordings.example.test/api/sessions/session-1/segments/part-1/download',
      'https://recordings.example.test/api/sessions/session-1/segments/part-2/download',
    ])
    expect(document.querySelectorAll('a[hidden][download]')).toHaveLength(0)
  })

  it('每个可下载分段保留独立链接，未完成分段没有下载地址', () =>
  {
    const wrapper = open([segment('ready'), segment('growing', { isClosed: false })])
    const links = wrapper.findAll('a[download]')
    expect(links).toHaveLength(1)
    expect(links[0]!.attributes('href')).toBe('https://recordings.example.test/api/sessions/session-1/segments/ready/download')
    expect(links[0]!.attributes('aria-label')).toBe('下载片段 01')
    expect(wrapper.find('a[href*="growing"]').exists()).toBe(false)
  })

  it.each([{ segments: [] }, { segments: [segment('growing', { isClosed: false })] }])('无已完成分段时不允许批量下载', async ({ segments }) =>
  {
    const wrapper = open(segments)
    expect(action(wrapper).attributes('disabled')).toBeDefined()
    await action(wrapper).trigger('click')
    expect(receivedDownloads).toEqual([])
    expect(wrapper.text()).toContain('暂无可下载分段')
  })

  it('重新打开恢复默认全选，并清除上次下载请求提示', async () =>
  {
    const wrapper = open()
    await action(wrapper).trigger('click')
    await wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')[0]!.setValue(false)
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true })
    expect(wrapper.findAll<HTMLInputElement>('input[type="checkbox"]').every(item => item.element.checked)).toBe(true)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('刷新分段状态会移除失效选择，新增已完成分段保持未选直到全选', async () =>
  {
    const wrapper = open()
    await wrapper.setProps({ segments: [segment('part-1', { isClosed: false }), segment('part-2'), segment('part-3')] })
    expect(wrapper.findAll<HTMLInputElement>('input[type="checkbox"]').map(item => item.element.checked)).toEqual([false, false, true, false])
    await action(wrapper).trigger('click')
    expect(receivedDownloads).toEqual(['https://recordings.example.test/api/sessions/session-1/segments/part-2/download'])
  })

  it('切换场次时重置选择并使用新的分段下载地址', async () =>
  {
    const wrapper = open()
    await wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')[0]!.setValue(false)
    await wrapper.setProps({ sessionId: 'session-2', segments: [segment('second', { sessionId: 'session-2' })] })
    await action(wrapper).trigger('click')
    expect(receivedDownloads).toEqual(['https://recordings.example.test/api/sessions/session-2/segments/second/download'])
  })

  it.each([
    ['zh-CN', '下载录播', '下载所选'], ['en-US', 'Download recording', 'Download selected'],
    ['ja-JP', '録画をダウンロード', '選択したセグメントをダウンロード'],
  ] as const)('%s 显示下载界面与操作文案', async (language, title, label) =>
  {
    const wrapper = open()
    i18n.global.locale.value = language
    await nextTick()
    expect(wrapper.findComponent(CardHeader).props('title')).toBe(title)
    expect(action(wrapper).text()).toBe(label)
  })

  it('下载地址对场次与分段 ID 分别编码', () =>
  {
    expect(MediaAPI.downloadSegment('session /中文', 'part ?#')).toBe('https://recordings.example.test/api/sessions/session%20%2F%E4%B8%AD%E6%96%87/segments/part%20%3F%23/download')
  })
})
