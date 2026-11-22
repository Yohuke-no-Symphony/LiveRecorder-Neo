// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, type Pinia } from 'pinia'
import { createVuetify } from 'vuetify'
import { VAlert } from 'vuetify/components/VAlert'
import { VAvatar } from 'vuetify/components/VAvatar'
import { VBtn } from 'vuetify/components/VBtn'
import { VCard, VCardText } from 'vuetify/components/VCard'
import { VDataTableServer } from 'vuetify/components/VDataTable'
import { VImg } from 'vuetify/components/VImg'
import { MediaAPI } from '../src/apis/MediaAPI'
import UserAvatar from '../src/components/common/UserAvatar.vue'
import { PERMISSIONS } from '../src/models/auth'
import type { ManagedClip } from '../src/models/clipManagement'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import ClipManagementView from '../src/views/ClipManagementView.vue'

vi.mock('../src/apis/MediaAPI', () => ({
  MediaAPI: { managedClips: vi.fn(), trashManagedClips: vi.fn(), downloadClip: (id: string) => `/api/clips/${id}/download` },
}))

const managedClips = vi.mocked(MediaAPI.managedClips)
const trashClips = vi.mocked(MediaAPI.trashManagedClips)
let pinia: Pinia
let wrapper: VueWrapper | undefined
let items: ManagedClip[]

function clip(id: string): ManagedClip
{
  return {
    id, ownerId: 'owner', ownerDisplayName: '所有者甲', ownerAvatarUrl: '/avatars/owner.png',
    sessionId: 'session', title: `切片 ${id}`, bytes: 128, startMs: 0, endMs: 90000,
    createdAtUtc: '2026-11-13T00:00:00Z', expiresAtUtc: '2026-12-13T00:00:00Z',
  }
}

async function open(): Promise<VueWrapper>
{
  wrapper = mount(ClipManagementView, {
    attachTo: document.body,
    global: {
      plugins: [pinia, i18n, createVuetify({ components: { VAlert, VAvatar, VBtn, VCard, VCardText, VDataTableServer, VImg } })],
      stubs: {
        ClipPreviewDialog: true, ClipShareDialog: true,
        VDialog: { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot /></section>' },
      },
    },
  })
  await flushPromises()
  return wrapper
}

async function confirm(scope: VueWrapper): Promise<void>
{
  const button = scope.get('[role="dialog"]').findAll('button').find(item => item.text() === '移入回收站')!
  await button.trigger('click')
  await flushPromises()
}

beforeEach(() =>
{
  pinia = createPinia()
  useAuthStore(pinia).user = {
    id: 'manager', displayName: '管理员委派账号', email: 'manager@example.test', role: 'User',
    status: 'Active', permissions: [PERMISSIONS.manageClips],
  }
  i18n.global.locale.value = 'zh-CN'
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  managedClips.mockReset().mockImplementation(async ({ page, pageSize }) => ({
    items: items.slice((page - 1) * pageSize, page * pageSize),
    total: items.length,
  }))
  trashClips.mockReset()
})

afterEach(() =>
{
  wrapper?.unmount()
  wrapper = undefined
  disposePinia(pinia)
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

describe('切片管理表', () =>
{
  it('显示所有者头像和红色回收按钮，仅对有媒体权限的账号开放播放和下载', async () =>
  {
    items = [clip('a')]
    const view = await open()
    const row = view.get('tbody tr')
    expect(row.getComponent(UserAvatar).props()).toMatchObject({ name: '所有者甲', avatarUrl: '/avatars/owner.png' })
    expect(row.get('button[aria-label="移入回收站"]').classes()).toEqual(expect.arrayContaining(['text-error', 'v-btn--variant-text']))
    expect(row.get('button[aria-label="播放"]').element).toHaveProperty('disabled', true)
    expect(row.get('button[aria-label="下载"]').element).toHaveProperty('disabled', true)
    await row.get('button[aria-label="切片分享"]').trigger('click')
    expect(view.findComponent({ name: 'ClipShareDialog' }).exists()).toBe(true)

    useAuthStore(pinia).user!.permissions.push(PERMISSIONS.viewRecordings, PERMISSIONS.downloadRecordings)
    await flushPromises()
    expect(row.get('button[aria-label="播放"]').element).toHaveProperty('disabled', false)
    expect(row.get('a[aria-label="下载"]').attributes('href')).toBe('/api/clips/a/download')
  })

  it('全选只影响当前页，切页和排序均清除选择', async () =>
  {
    items = Array.from({ length: 26 }, (_, index) => clip(String(index + 1)))
    const view = await open()
    expect(managedClips).toHaveBeenLastCalledWith(
      { page: 1, pageSize: 25, sort: 'createdAtUtc', direction: 'desc' }, expect.any(AbortSignal),
    )
    await view.get('thead input[type="checkbox"]').setValue(true)
    expect(view.text()).toContain('移入回收站（25）')
    await view.get('.v-pagination__next button').trigger('click')
    await flushPromises()
    expect(managedClips).toHaveBeenLastCalledWith(
      { page: 2, pageSize: 25, sort: 'createdAtUtc', direction: 'desc' }, expect.any(AbortSignal),
    )
    expect(view.get('tbody').text()).toContain('切片 26')
    expect(view.text()).toContain('移入回收站（0）')
    await view.get('tbody input[type="checkbox"]').trigger('click')
    expect(view.text()).toContain('移入回收站（1）')
    const sizeHeader = view.findAll('th').find(header => header.text() === '大小')!
    await sizeHeader.trigger('click')
    await flushPromises()
    expect(managedClips).toHaveBeenLastCalledWith(
      { page: 1, pageSize: 25, sort: 'bytes', direction: 'asc' }, expect.any(AbortSignal),
    )
    expect(view.text()).toContain('移入回收站（0）')

    for (const [title, sort] of [['所有者', 'ownerDisplayName'], ['时长', 'duration']])
    {
      await view.findAll('th').find(header => header.text() === title)!.trigger('click')
      await flushPromises()
      expect(managedClips).toHaveBeenLastCalledWith(
        { page: 1, pageSize: 25, sort, direction: 'asc' }, expect.any(AbortSignal),
      )
    }
  })

  it('批量回收先确认，部分失败后只重试失败项', async () =>
  {
    items = [clip('a'), clip('b')]
    trashClips.mockImplementationOnce(async () =>
    {
      items = [clip('b')]
      return [{ clipId: 'a', success: true }, { clipId: 'b', success: false, error: '文件暂时被占用' }]
    }).mockImplementationOnce(async () =>
    {
      items = []
      return [{ clipId: 'b', success: true }]
    })
    const view = await open()
    await view.get('thead input[type="checkbox"]').setValue(true)
    await view.findAll('button').find(button => button.text() === '移入回收站（2）')!.trigger('click')
    expect(view.get('[role="dialog"]').text()).toContain('将 2 条切片移入回收站。')
    expect(trashClips).not.toHaveBeenCalled()
    await confirm(view)
    expect(trashClips).toHaveBeenNthCalledWith(1, ['a', 'b'])
    expect(view.get('[role="dialog"]').text()).toContain('切片 b：文件暂时被占用')
    expect(view.get('tbody').text()).not.toContain('切片 a')
    await confirm(view)
    expect(trashClips).toHaveBeenNthCalledWith(2, ['b'])
    expect(view.find('[role="dialog"]').exists()).toBe(false)
    expect(view.get('tbody').text()).toContain('暂无切片')
  })
})
