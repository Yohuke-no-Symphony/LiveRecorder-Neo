// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import type { ServerSettings } from '../src/models/settings'
import SettingsView from '../src/views/SettingsView.vue'

vi.mock('../src/apis/MediaAPI', () => ({
  MediaAPI: {
    settings: vi.fn(),
    updateSettings: vi.fn(),
    settingsPaths: vi.fn(async () => ({ dataDirectory: '/srv/Data', recordsDirectory: '/srv/Data/records', cacheDirectory: '/srv/Data/cache' })),
  },
}))

const container = { template: '<div><slot /></div>' }
const tabs = defineComponent({
  props: ['modelValue'],
  emits: ['update:modelValue'],
  setup(_, { slots })
  {
    return () => h('div', slots.default?.())
  },
})
const toggle = defineComponent({
  props: ['modelValue', 'label', 'hint'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      type: 'checkbox', checked: props.modelValue, 'aria-label': props.label,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).checked),
    }), props.hint])
  },
})
const stubs = {
  VTabs: tabs, VTab: container, VCard: container, VSwitch: toggle, VTextField: true, VSelect: true, VDivider: true,
  VBtn: { template: '<button><slot /></button>' }, VAlert: container, VProgressLinear: true, VSnackbar: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia

function settings(checkCertificateRevocation: boolean): ServerSettings
{
  const value = {
    id: 'server', defaultLanguage: 'zh-CN' as const, storagePath: '/srv/recordings', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, clipRetentionDays: 7, trashRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    registrationEnabled: true, ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', oauthProviders: [],
    smtp: { host: 'smtp.example.test', port: 465, useSsl: true, checkCertificateRevocation, userName: 'sender@example.test', password: '', fromAddress: 'sender@example.test', fromName: 'Recorder' },
  }
  return value
}

async function openMailSettings(): Promise<VueWrapper>
{
  const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'smtp')
  await nextTick()
  return wrapper
}

beforeEach(() =>
{
  vi.clearAllMocks()
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  i18n.global.locale.value = 'zh-CN'
  vi.unstubAllGlobals()
})

describe('SMTP 证书吊销检查', () =>
{
  it.each([true, false])('读取 %s，保存切换后的值并在重新加载后保持', async initial =>
  {
    let current = settings(initial)
    vi.spyOn(MediaAPI, 'settings').mockImplementation(async () => JSON.parse(JSON.stringify(current)) as ServerSettings)
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockImplementation(async value =>
    {
      current = JSON.parse(JSON.stringify(value)) as ServerSettings
      return JSON.parse(JSON.stringify(current)) as ServerSettings
    })
    const wrapper = await openMailSettings()
    const selector = 'input[aria-label="检查证书吊销状态"]'
    expect(wrapper.get<HTMLInputElement>(selector).element.checked).toBe(initial)

    await wrapper.get(selector).setValue(!initial)
    await wrapper.get('header button').trigger('click')
    await flushPromises()

    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
      smtp: expect.objectContaining({ checkCertificateRevocation: !initial, useSsl: true, port: 465 }),
    }))
    expect(wrapper.get<HTMLInputElement>(selector).element.checked).toBe(!initial)
  })

  it.each([
    ['zh-CN', '检查证书吊销状态', '无法确认它是否已被吊销'],
    ['en-US', 'Check certificate revocation', 'cannot determine whether it has been revoked'],
    ['ja-JP', '証明書の失効状態を確認', '失効の有無は確認できません'],
  ] as const)('%s 显示开关与关闭后的影响', async (language, label, explanation) =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings(true))
    const wrapper = await openMailSettings()
    i18n.global.locale.value = language
    await nextTick()

    expect(wrapper.get<HTMLInputElement>(`input[aria-label="${label}"]`).element.checked).toBe(true)
    expect(wrapper.text()).toContain(explanation)
  })
})
