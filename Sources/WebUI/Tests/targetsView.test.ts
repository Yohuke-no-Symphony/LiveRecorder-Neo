// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createVuetify } from 'vuetify'
import { VDataTable } from 'vuetify/components/VDataTable'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import type { BilibiliAccount } from '../src/models/auth'
import type { Streamer } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import TargetsView from '../src/views/TargetsView.vue'

let wrapper: VueWrapper | undefined

function target(id: string, overrides: Partial<Streamer> = {}): Streamer
{
  return {
    id, createdAtUtc: '2026-11-13T00:00:00Z', uid: 42, roomId: 42, name: id, avatarUrl: '',
    enabled: false, isMonitored: true, isLive: false, state: 'Idle', ...overrides,
  }
}

function account(id: string, userName: string): BilibiliAccount
{
  return { id, userName, uid: 42, avatarUrl: '', status: 'Valid' }
}

beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
})

afterEach(() =>
{
  wrapper?.unmount()
  wrapper = undefined
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

it('录制账号按显示名排序，排序后仍对对应主播操作', async () =>
{
  const original = target('Zulu', { accountId: '1', quality: 10000, enabled: true })
  const smooth = target('Alpha', { accountId: '9', quality: 80 })
  const unmonitored = target('Excluded', { isMonitored: false })
  vi.spyOn(MediaAPI, 'streamers').mockResolvedValueOnce([original, smooth, unmonitored])
    .mockResolvedValue([original, { ...smooth, enabled: true }, unmonitored])
  vi.spyOn(AuthAPI, 'bilibiliAccounts').mockResolvedValue([account('1', 'Zulu 账号'), account('9', 'Alpha 账号')])
  const update = vi.spyOn(MediaAPI, 'updateStreamer').mockResolvedValue({ ...smooth, enabled: true })
  wrapper = mount(TargetsView, {
    global: {
      plugins: [i18n, createVuetify({ components: { VDataTable } })],
      stubs: {
        RouterLink: { template: '<a><slot /></a>' },
        VCard: { template: '<div><slot /></div>' }, VAvatar: true, VImg: true, VBtn: true,
        VDialog: true, ConfirmDialog: true, StatusChip: true,
        VSwitch: {
          props: ['modelValue', 'disabled'], emits: ['update:modelValue'],
          template: '<input type="checkbox" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />',
        },
      },
    },
  })
  await flushPromises()
  expect(wrapper.get('tbody').text()).not.toContain(unmonitored.name)
  expect(wrapper.get('tbody tr').text()).toContain('Zulu 账号')
  await wrapper.findAll('th').find(header => header.text() === '录制账号')!.trigger('click')
  expect(wrapper.get('tbody tr').text()).toContain('Alpha 账号')
  await wrapper.get('tbody tr input[type="checkbox"]').setValue(true)
  await flushPromises()
  expect(update).toHaveBeenCalledExactlyOnceWith(smooth.id, expect.objectContaining({ enabled: true, accountId: '9' }))
  const quality = wrapper.findAll('th').find(header => header.text() === '清晰度')!
  await quality.trigger('click')
  expect(wrapper.get('tbody tr').text()).toContain('Alpha 账号')
  await quality.trigger('click')
  expect(wrapper.get('tbody tr').text()).toContain('Zulu 账号')
})
