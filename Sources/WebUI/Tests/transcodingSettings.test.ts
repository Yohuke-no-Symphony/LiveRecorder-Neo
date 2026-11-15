// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import { defaultTranscodingSettings, defaultWorkerConfiguration, isValidTranscodingSettings, isValidWorkerConfiguration } from '../src/models/transcoding'
import type { ServerSettings } from '../src/models/settings'
import SettingsView from '../src/views/SettingsView.vue'

vi.mock('../src/apis/MediaAPI', () => ({
  MediaAPI: {
    settings: vi.fn(), updateSettings: vi.fn(),
    settingsPaths: vi.fn(async () => ({ dataDirectory: '/srv/Data', recordsDirectory: '/srv/records', cacheDirectory: '/srv/Data/cache', configFile: '/srv/settings.json', configuredDataDirectory: 'Data', configuredRecordsDirectory: 'records', restartRequired: false })),
  },
}))

const sortableInstances = vi.hoisted(() => [] as { element: HTMLElement; options: { onEnd: (event: { from: HTMLElement; item: HTMLElement; oldIndex: number; newIndex: number }) => void }; option: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn> }[])
vi.mock('sortablejs', () => ({ default: class { option = vi.fn(); destroy = vi.fn(); constructor(public element: HTMLElement, public options: typeof sortableInstances[number]['options']) { sortableInstances.push(this) } } }))

const container = { template: '<div><slot /></div>' }
const tabs = defineComponent({
  props: ['modelValue'],
  emits: ['update:modelValue'],
  setup(_, { slots })
  {
    return () => h('div', slots.default?.())
  },
})
const field = defineComponent({
  props: ['modelValue', 'label', 'type', 'disabled', 'min', 'max', 'step', 'errorMessages'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('input', {
      value: props.modelValue, 'aria-label': props.label, type: props.type ?? 'text', disabled: props.disabled,
      min: props.min, max: props.max, step: props.step, 'data-error': props.errorMessages || undefined,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    })
  },
})
const toggle = defineComponent({
  props: ['modelValue', 'label', 'disabled'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('input', {
      type: 'checkbox', checked: props.modelValue, 'aria-label': props.label, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).checked),
    })
  },
})
const stubs = {
  VTabs: tabs, VTab: container, VTextField: field, VSwitch: toggle, VSelect: true, VProgressLinear: true,
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /></div>' },
  ContentCard: { template: '<section><slot name="header-actions" /><slot /><slot name="actions" /></section>' }, DataDirectorySettings: true, LanguageSelect: true, TranscodingWorkersPanel: true,
  VTooltip: { template: '<span><slot name="activator" :props="{}" /></span>' },
}
const mounted: VueWrapper[] = []
let pinia: Pinia
let current: ServerSettings

function serverSettings(): ServerSettings
{
  return {
    id: 'server', defaultLanguage: 'zh-CN', translationDanmuPattern: '.*?【.*?】', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, recordingRetentionDays: 0, clipRetentionDays: 7, trashRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    streamDisconnectTimeoutMinutes: 5, defaultUserPermissions: [], registrationEnabled: true,
    registrationApprovalRequired: true, registrationInvitationRequired: true, registrationEmailVerificationRequired: true,
    ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', oauthProviders: [],
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
  }
}

async function openTranscoding(): Promise<VueWrapper>
{
  const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  expect(wrapper.text()).toContain('转码')
  wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'transcoding')
  await nextTick()
  return wrapper
}

async function save(wrapper: VueWrapper): Promise<void>
{
  await wrapper.get('header button').trigger('click')
  await flushPromises()
}

beforeEach(() =>
{
  vi.clearAllMocks()
  sortableInstances.length = 0
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  current = serverSettings()
  vi.mocked(MediaAPI.settings).mockImplementation(async () => structuredClone(current))
  vi.mocked(MediaAPI.updateSettings).mockImplementation(async value =>
  {
    current = JSON.parse(JSON.stringify(value)) as ServerSettings
    return structuredClone(current)
  })
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  i18n.global.locale.value = 'zh-CN'
})


describe('清晰度设置', () =>
{
  it('旧配置默认720p，原画可设默认，编辑与排序在保存前不写后台', async () =>
  {
    const wrapper = await openTranscoding()
    expect(wrapper.get('[aria-label="将 720p 设为默认播放"]').attributes('aria-pressed')).toBe('true')
    await wrapper.get('[aria-label="将 原画 设为默认播放"]').trigger('click')
    await wrapper.get('[aria-label="调整 720p 的顺序"]').trigger('keydown', { key: 'ArrowUp' })
    expect(wrapper.findAll('[data-sortable-id]').map(row => row.attributes('data-sortable-id'))).toEqual(['720p', 'original'])
    expect(wrapper.get('[aria-label="将 原画 设为默认播放"]').attributes('aria-pressed')).toBe('true')
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(current.transcoding?.qualityOrder).toEqual(['720p', 'original'])
    expect(current.transcoding?.defaultPlaybackQualityId).toBe('original')
  })

  it('Sortable拖拽后恢复Vue接管的DOM位置，保存稳定ID顺序而不改默认项', async () =>
  {
    const wrapper = await openTranscoding()
    const instance = sortableInstances[0]!
    const item = instance.element.children[0] as HTMLElement
    instance.element.appendChild(item)
    instance.options.onEnd({ from: instance.element, item, oldIndex: 0, newIndex: 1 })
    await nextTick()
    expect(wrapper.findAll('[data-sortable-id]').map(row => row.attributes('data-sortable-id'))).toEqual(['720p', 'original'])
    await save(wrapper)
    expect(current.transcoding?.qualityOrder).toEqual(['720p', 'original'])
    expect(current.transcoding?.defaultPlaybackQualityId).toBe('720p')
  })

  it('删除当前默认档位回原画，原画不显示删除按钮，空档列表可保存', async () =>
  {
    const wrapper = await openTranscoding()
    expect(wrapper.find('[aria-label="删除 原画"]').exists()).toBe(false)
    await wrapper.get('[aria-label="删除 720p"]').trigger('click')
    await save(wrapper)
    expect(current.transcoding).toEqual({ enabled: true, profiles: [], qualityOrder: ['original'], defaultPlaybackQualityId: 'original' })
  })

  it('新增档位使用稳定ID，清空帧率保留源帧率并可重新读取', async () =>
  {
    const wrapper = await openTranscoding()
    const add = wrapper.findAll('button').find(button => button.text() === '添加清晰度')!
    await add.trigger('click')
    const frameRate = wrapper.findAll('input[aria-label="帧率上限（fps）"]')[1]!
    await frameRate.setValue('30')
    await frameRate.setValue('')
    await save(wrapper)
    const added = current.transcoding!.profiles[1]!
    expect(added.id).not.toBe('720p')
    expect(added.name).toBe('1080p')
    expect(added.maxFrameRate).toBeNull()
    const reopened = await openTranscoding()
    expect(reopened.find(`[data-sortable-id="${added.id}"]`).exists()).toBe(true)
  })

  it('保存失败保留排序与编辑，保存期间禁用输入和拖拽', async () =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="名称"]').setValue('手机省流')
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('保存失败'))
    await save(wrapper)
    expect(wrapper.get('input[aria-label="名称"]').element).toHaveProperty('value', '手机省流')
    expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
    let finish!: (value: ServerSettings) => void
    vi.mocked(MediaAPI.updateSettings).mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
    await wrapper.get('header button').trigger('click')
    expect(wrapper.findAll('input, .reorder-handle').every(input => input.attributes('disabled') !== undefined)).toBe(true)
    expect(sortableInstances[0]!.option).toHaveBeenCalledWith('disabled', true)
    finish(structuredClone(current))
    await flushPromises()
  })

  it.each(['63', '721', '8642', ''])('拒绝无效分辨率 %s，并保持草稿', async value =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="分辨率短边（px）"]').setValue(value)
    await save(wrapper)
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('请检查清晰度设置')
  })

  it.each([['en-US', 'Resolution short edge (px)'], ['ja-JP', '解像度の短辺（px）']] as const)('%s 包含清晰度控件文案', async (locale, label) =>
  {
    const wrapper = await openTranscoding()
    i18n.global.locale.value = locale
    await nextTick()
    expect(wrapper.find(`input[aria-label="${label}"]`).exists()).toBe(true)
    expect(wrapper.text()).not.toContain('transcoding.')
  })

  it('清晰度和Worker校验与主站边界一致，禁止多个默认身份和无资源配置', () =>
  {
    expect(isValidTranscodingSettings(defaultTranscodingSettings())).toBe(true)
    const settings = defaultTranscodingSettings()
    settings.qualityOrder = ['original', 'original']
    expect(isValidTranscodingSettings(settings)).toBe(false)
    const worker = defaultWorkerConfiguration()
    expect(isValidWorkerConfiguration(worker)).toBe(true)
    expect(worker).toMatchObject({ cpuFallbackMode: 'Balanced', gpuWaitSeconds: 60, cpuSampleWindowSeconds: 30, cpuLoadThreshold: 60, maxConcurrentEncodes: 2, cpuConcurrency: 1, gpuConcurrency: 1, cpuThreadFraction: .5 })
    worker.gpuConcurrency = worker.cpuConcurrency = 0
    expect(isValidWorkerConfiguration(worker)).toBe(false)
    worker.gpuConcurrency = 1
    worker.cpuSampleWindowSeconds = 4
    expect(isValidWorkerConfiguration(worker)).toBe(false)
  })
})
