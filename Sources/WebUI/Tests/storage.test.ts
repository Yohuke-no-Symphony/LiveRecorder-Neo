// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import StorageBar from '../src/components/common/StorageBar.vue'
import TrashView from '../src/views/TrashView.vue'
import StorageView from '../src/views/StorageView.vue'
import type { StorageVolume } from '../src/models/media'
import type { StorageUsageReport } from '../src/models/storage'
import { storageSegments } from '../src/utilities/storage'
import { i18n } from '../src/plugins/i18n'

const { storage, storageDetails, trash } = vi.hoisted(() => ({ storage: vi.fn(), storageDetails: vi.fn(), trash: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { storage, storageDetails, trash } }))
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => ({ isAdmin: true, canManageRecordings: true, canPurgeRecordings: true }) }))

const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }
const stubs = {
  VCard: container, VChip: container, VTable: { template: '<table><slot /></table>' }, VTabs: { name: 'VTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div><slot /></div>' },
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

function render(component: Parameters<typeof mount>[0], props = {}): VueWrapper
{
  const wrapper = mount(component, { props, global: { plugins: [i18n], stubs } })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() =>
{
  vi.useFakeTimers()
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
})

describe('磁盘分类', () =>
{
  it('五类容量与比例一致，零容量和异常数据也不会产生无效宽度', () =>
  {
    const segments = storageSegments(volume())
    expect(segments.map(item => [item.id, item.bytes, item.percent])).toEqual([
      ['recordings', 200, 20], ['clips', 150, 15], ['trash', 100, 10], ['other', 150, 15], ['free', 400, 40],
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
    expect(wrapper.findAll('.storage-segment')).toHaveLength(5)
    expect(wrapper.find('[data-category="clips"]').attributes('style')).toContain('width: 15%')
    expect(wrapper.text()).toContain('录播空间')
    expect(wrapper.text()).not.toContain('个人切片')
    await wrapper.get('.storage-block').trigger('mouseenter')
    for (const label of ['录播文件', '个人切片', '回收站', '其他占用']) { expect(wrapper.get('[role="tooltip"]').text()).toContain(label) }
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
