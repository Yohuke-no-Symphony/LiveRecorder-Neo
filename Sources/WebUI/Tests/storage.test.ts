// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { createVuetify } from 'vuetify'
import { VDataTable } from 'vuetify/components/VDataTable'
import StorageBar from '../src/components/common/StorageBar.vue'
import TrashView from '../src/views/TrashView.vue'
import StorageView from '../src/views/StorageView.vue'
import type { StorageVolume } from '../src/models/media'
import type { CacheCleanupResult, StorageUsageReport } from '../src/models/storage'
import { storageSegments } from '../src/utilities/storage'
import { i18n } from '../src/plugins/i18n'

const { storage, storageDetails, trash } = vi.hoisted(() => ({ storage: vi.fn(), storageDetails: vi.fn(), trash: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { storage, storageDetails, trash } }))
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => ({ isAdmin: true, canManageRecordings: true, canPurgeRecordings: true }) }))

const mounted: VueWrapper[] = []
const vuetify = createVuetify({ components: { VDataTable } })
const container = { template: '<div><slot /></div>' }
const cleanupDialog = {
  name: 'StorageCacheCleanupDialog', props: ['modelValue'], emits: ['update:modelValue', 'cleaned', 'busy'],
  template: '<div :data-cleanup-open="modelValue" />',
}
const stubs = {
  StorageCacheCleanupDialog: cleanupDialog,
  VCard: container, VChip: container, VTabs: { name: 'VTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div><slot /></div>' },
  VTab: { props: ['value'], template: '<button @click="$parent.$emit(\'update:modelValue\', value)"><slot /></button>' },
  VTooltip: {
    props: ['disabled'], data: () => ({ open: false }),
    template: '<div><slot name="activator" :props="{ onMouseenter: () => open = true, onFocus: () => open = true }" /><div v-if="open && !disabled" role="tooltip"><slot /></div></div>',
  },
  VIcon: true, VDialog: true, EmptyState: { props: ['title'], template: '<div>{{ title }}</div>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VTextField: { props: ['modelValue'], emits: ['update:modelValue'], template: '<input :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />' },
}

function volume(id = 'recordings'): StorageVolume
{
  return { id, path: `/private/${id}`, totalBytes: 1000, usedBytes: 500, freeBytes: 400, trashBytes: 100, cacheBytes: 0, recordingBytes: 200, clipBytes: 150, otherBytes: 150 }
}

function report(): StorageUsageReport
{
  const row = { id: 'room-42', name: '测试直播间', path: '/records/42', recordingBytes: 200, clipBytes: 150, trashBytes: 0, otherBytes: 0, totalBytes: 350 }
  return {
    scannedAtUtc: '2026-11-13T08:00:00Z', volumes: [volume()],
    rooms: [{ ...row, roomId: 42 }],
    users: [{ ...row, id: 'owner', ownerId: 'owner', name: '切片用户', path: '', recordingBytes: 0, totalBytes: 150 }],
    directories: [{ ...row, id: 'trash', kind: 'trash', name: '', path: '/records/.trash', recordingBytes: 0, clipBytes: 0, trashBytes: 100, totalBytes: 100 }],
  }
}

function cleanupResult(): CacheCleanupResult
{
  return {
    categories: [{ category: 'playback', deletedFiles: 1, deletedBytes: 2048, skippedEntries: 0, failedEntries: 0 }],
    deletedFiles: 1, deletedBytes: 2048, skippedEntries: 0, failedEntries: 0,
  }
}

function render(component: Parameters<typeof mount>[0], props = {}): VueWrapper
{
  const wrapper = mount(component, { props, global: { plugins: [i18n, vuetify], stubs } })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() =>
{
  vi.useFakeTimers()
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.clearAllMocks()
  i18n.global.locale.value = 'zh-CN'
  storage.mockResolvedValue([volume(), volume('data')])
  storageDetails.mockResolvedValue(report())
  trash.mockResolvedValue([])
})

afterEach(() =>
{
  for (const wrapper of mounted.splice(0)) { wrapper.unmount() }
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('磁盘分类', () =>
{
  it('六类容量与比例一致，零容量和异常数据也不会产生无效宽度', () =>
  {
    const segments = storageSegments(volume())
    expect(segments.map(item => [item.id, item.bytes, item.percent])).toEqual([
      ['recordings', 200, 20], ['derived', 0, 0], ['clips', 150, 15], ['trash', 100, 10], ['other', 150, 15], ['free', 400, 40],
    ])
    for (const input of [
      { ...volume(), totalBytes: 0 },
      { ...volume(), freeBytes: -1, recordingBytes: 2000, clipBytes: Number.NaN },
    ])
    {
      const values = storageSegments(input)
      expect(values.every(item => Number.isFinite(item.percent) && item.percent >= 0)).toBe(true)
      expect(values.reduce((sum, item) => sum + item.percent, 0)).toBeLessThanOrEqual(100.000001)
    }
  })

  it('左下角保持精简，将分类详情放在悬停或聚焦的tooltip中', async () =>
  {
    const wrapper = render(StorageBar, { volume: volume() })
    expect(wrapper.get('[role="meter"]').attributes('aria-valuenow')).toBe('60')
    expect(wrapper.findAll('.storage-segment')).toHaveLength(6)
    expect(wrapper.find('[data-category="clips"]').attributes('style')).toContain('width: 15%')
    expect(wrapper.text()).toContain('录播空间')
    expect(wrapper.text()).not.toContain('个人切片')
    await wrapper.get('.storage-block').trigger('mouseenter')
    for (const label of ['录播文件', '转码成品', '个人切片', '回收站', '其他占用']) { expect(wrapper.get('[role="tooltip"]').text()).toContain(label) }
  })

  it('转码成品独立计入容量，兼容缺少新字段的旧响应', () =>
  {
    const segments = storageSegments({ ...volume(), derivedBytes: 50, otherBytes: 100 })
    expect(segments.find(item => item.id === 'derived')).toEqual({ id: 'derived', bytes: 50, percent: 5 })
    expect(segments.find(item => item.id === 'other')?.percent).toBe(10)
    expect(segments.reduce((sum, item) => sum + item.percent, 0)).toBe(100)
  })

  it('键盘聚焦侧栏占用条也能打开分类详情', async () =>
  {
    const wrapper = render(StorageBar, { volume: volume() })
    expect(wrapper.get('.storage-block').attributes('tabindex')).toBe('0')
    await wrapper.get('.storage-block').trigger('focus')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('个人切片')
  })

  it('回收站仅显示录播空间卡片且不暴露目录路径', async () =>
  {
    const wrapper = render(TrashView)
    await flushPromises()
    expect(wrapper.findAllComponents(StorageBar)).toHaveLength(1)
    expect(wrapper.findComponent(StorageBar).props('volume').id).toBe('recordings')
    expect(wrapper.text()).toContain('录播空间')
    expect(wrapper.text()).not.toContain('/private/')
    await wrapper.findAll('button').find(item => item.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(storage).toHaveBeenLastCalledWith(true)
  })

  it('分类名称随界面语言切换', async () =>
  {
    const wrapper = render(StorageBar, { volume: volume(), detailed: true })
    for (const [locale, label] of [['en-US', 'Personal clips'], ['ja-JP', '個人クリップ'], ['zh-CN', '个人切片']] as const)
    {
      i18n.global.locale.value = locale
      await nextTick()
      expect(wrapper.text()).toContain(label)
    }
  })
})

describe('管理员磁盘明细', () =>
{
  it('页头可以打开缓存清理对话框，成功后强制刷新磁盘明细且保留结果弹窗', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    const dialog = wrapper.findComponent(cleanupDialog)
    expect(dialog.props('modelValue')).toBe(false)
    await wrapper.findAll('button').find(item => item.text() === i18n.global.t('views.storage.cleanup.title'))!.trigger('click')
    expect(dialog.props('modelValue')).toBe(true)
    const refreshed = report()
    refreshed.rooms[0]!.name = '清理后的直播间'
    storageDetails.mockResolvedValueOnce(refreshed)

    dialog.vm.$emit('cleaned', cleanupResult())
    await flushPromises()

    expect(storageDetails).toHaveBeenCalledTimes(2)
    expect(storageDetails).toHaveBeenLastCalledWith(true)
    expect(wrapper.text()).toContain('清理后的直播间')
    expect(dialog.props('modelValue')).toBe(true)
  })

  it('清理成功后的统计刷新失败只在父页显示错误，不关闭或重建成功弹窗', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    await wrapper.findAll('button').find(item => item.text() === i18n.global.t('views.storage.cleanup.title'))!.trigger('click')
    const dialog = wrapper.findComponent(cleanupDialog)
    const instance = dialog.vm
    storageDetails.mockRejectedValueOnce(new Error('清理后的统计刷新失败'))

    dialog.vm.$emit('cleaned', cleanupResult())
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('清理后的统计刷新失败')
    expect(wrapper.text()).toContain('测试直播间')
    expect(wrapper.findComponent(cleanupDialog).vm).toBe(instance)
    expect(dialog.props('modelValue')).toBe(true)
    expect(storageDetails).toHaveBeenLastCalledWith(true)
  })

  it('清理期间跳过轮询并禁用页头操作，结束后使用强制刷新', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    const dialog = wrapper.findComponent(cleanupDialog)
    dialog.vm.$emit('busy', true)
    await nextTick()
    const actions = wrapper.findAll<HTMLButtonElement>('.page-actions button')
    expect(actions).toHaveLength(2)
    expect(actions.every(item => item.element.disabled)).toBe(true)

    await vi.advanceTimersByTimeAsync(30000)
    await flushPromises()
    expect(storageDetails).toHaveBeenCalledTimes(1)
    dialog.vm.$emit('busy', false)
    dialog.vm.$emit('cleaned', cleanupResult())
    await flushPromises()
    expect(storageDetails).toHaveBeenCalledTimes(2)
    expect(storageDetails).toHaveBeenLastCalledWith(true)
  })

  it('清理完成时已有轮询未结束，等待旧请求完成后再发起强制刷新', async () =>
  {
    let finishPolling!: (value: StorageUsageReport) => void
    const polling = new Promise<StorageUsageReport>(resolve => { finishPolling = resolve })
    const wrapper = render(StorageView)
    await flushPromises()
    await wrapper.findAll('button').find(item => item.text() === i18n.global.t('views.storage.cleanup.title'))!.trigger('click')
    storageDetails.mockReturnValueOnce(polling)
    const refreshed = report()
    refreshed.rooms[0]!.name = '排队刷新后的直播间'
    storageDetails.mockResolvedValueOnce(refreshed)
    await vi.advanceTimersByTimeAsync(30000)
    expect(storageDetails).toHaveBeenCalledTimes(2)
    expect(storageDetails).toHaveBeenLastCalledWith(false)

    wrapper.findComponent(cleanupDialog).vm.$emit('cleaned', cleanupResult())
    await flushPromises()
    expect(storageDetails).toHaveBeenCalledTimes(2)
    finishPolling(report())
    await flushPromises()

    expect(storageDetails.mock.calls.map(call => call[0])).toEqual([false, false, true])
    expect(wrapper.text()).toContain('排队刷新后的直播间')
    expect(wrapper.findComponent(cleanupDialog).props('modelValue')).toBe(true)
  })

  it('显示扫描时间、直播间占用，并支持主动重新扫描', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    expect(wrapper.text()).toContain('磁盘')
    expect(wrapper.text()).toContain('测试直播间')
    expect(wrapper.text()).toContain('350 B')
    const refresh = wrapper.findAll('button').find(item => item.text() === '刷新')!
    await refresh.trigger('click')
    await flushPromises()
    expect(storageDetails).toHaveBeenLastCalledWith(true)
  })

  it('用户和目录页复用明细表，目录页显示回收站', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    wrapper.findComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'users')
    await nextTick()
    expect(wrapper.text()).toContain('切片用户')
    wrapper.findComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'directories')
    await nextTick()
    expect(wrapper.text()).toContain('/records/.trash')
    expect(wrapper.text()).toContain('100 B')
  })

  it('默认按合计降序，各容量列按字节比较并将缺失项视为零', async () =>
  {
    const data = report()
    const room = data.rooms[0]!
    data.rooms = [
      room,
      { ...room, id: 'large', name: 'Large', derivedBytes: 1024, totalBytes: 2048 },
      { ...room, id: 'small', name: 'Small', derivedBytes: 512, totalBytes: 512 },
    ]
    storageDetails.mockResolvedValue(data)
    const wrapper = render(StorageView)
    await flushPromises()
    const total = wrapper.findAll('th').find(header => header.text() === '合计')!
    expect(total.attributes('aria-sort')).toBe('descending')
    expect(wrapper.get('tbody tr').text()).toContain('Large')
    await total.trigger('click')
    expect(wrapper.get('tbody tr').text()).toContain('测试直播间')

    await wrapper.findAll('th').find(header => header.text() === '转码成品')!.trigger('click')
    expect(wrapper.findAll('tbody tr').map(row => row.text())).toEqual([
      expect.stringContaining('测试直播间'), expect.stringContaining('Small'), expect.stringContaining('Large'),
    ])
  })

  it('刷新失败提示错误，同时保留上次成功的明细', async () =>
  {
    const wrapper = render(StorageView)
    await flushPromises()
    storageDetails.mockRejectedValueOnce(new Error('统计失败'))
    await wrapper.findAll('button').find(item => item.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('统计失败')
    expect(wrapper.text()).toContain('测试直播间')
  })
})
