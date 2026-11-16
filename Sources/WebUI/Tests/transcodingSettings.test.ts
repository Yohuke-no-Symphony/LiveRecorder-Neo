// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import { defaultTranscodingSettings, defaultWorkerConfiguration, isValidTranscodingSettings, isValidWorkerConfiguration, normalizeTranscodingSettings } from '../src/models/transcoding'
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
  VTabs: tabs, VTab: container, VTextField: field, VSwitch: toggle, VSelect: true, VProgressLinear: true, VIcon: true,
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
  it('切片设置入口将正确的节点类型传给共享管理面板', async () =>
  {
    const wrapper = await openTranscoding()
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'clipping')
    await nextTick()
    expect(wrapper.find('transcoding-workers-panel-stub').attributes('kind')).toBe('Clipping')
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
  })

  it('旧配置默认720p，原画可设默认，编辑与排序在保存前不写后台', async () =>
  {
    const wrapper = await openTranscoding()
    expect(wrapper.get('[data-sortable-id="original"]').find('input[aria-label="转码优先级"]').exists()).toBe(false)
    expect(wrapper.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '0')
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
    expect(added).toMatchObject({ segmentDurationSeconds: 6, keyframeIntervalSeconds: 2, singleFile: false, autoTranscode: false, priority: 0 })
    const reopened = await openTranscoding()
    expect(reopened.find(`[data-sortable-id="${added.id}"]`).exists()).toBe(true)
  })

  it('各档位独立保存自动转码偏好，排序后仍跟随稳定ID并保留默认播放档位', async () =>
  {
    const wrapper = await openTranscoding()
    expect(wrapper.get('[data-sortable-id="original"]').find('input[aria-label="自动转码"]').exists()).toBe(false)
    expect(wrapper.get('[data-sortable-id="720p"] input[aria-label="自动转码"]').element).toHaveProperty('checked', true)
    await wrapper.findAll('button').find(button => button.text() === '添加清晰度')!.trigger('click')
    const addedRow = wrapper.findAll('[data-sortable-id]')[2]!
    const addedId = addedRow.attributes('data-sortable-id')!
    expect(addedRow.get('input[aria-label="自动转码"]').element).toHaveProperty('checked', false)
    await wrapper.get('[data-sortable-id="720p"] input[aria-label="自动转码"]').setValue(false)
    await addedRow.get('input[aria-label="自动转码"]').setValue(true)
    await addedRow.get('.reorder-handle').trigger('keydown', { key: 'Home' })
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()

    await save(wrapper)

    expect(current.transcoding!.qualityOrder).toEqual([addedId, 'original', '720p'])
    expect(current.transcoding!.defaultPlaybackQualityId).toBe('720p')
    expect(current.transcoding!.profiles.find(profile => profile.id === '720p')).toMatchObject({ enabled: true, autoTranscode: false })
    expect(current.transcoding!.profiles.find(profile => profile.id === addedId)).toMatchObject({ enabled: true, autoTranscode: true })
    const reopened = await openTranscoding()
    expect(reopened.get('[data-sortable-id="720p"] input[aria-label="自动转码"]').element).toHaveProperty('checked', false)
    expect(reopened.get(`[data-sortable-id="${addedId}"] input[aria-label="自动转码"]`).element).toHaveProperty('checked', true)
  })

  it('各档位独立保存转码优先级，播放顺序和默认档位互不影响', async () =>
  {
    current.transcoding = defaultTranscodingSettings()
    const original = structuredClone(current.transcoding)
    const wrapper = await openTranscoding()
    const priority = wrapper.get('[data-sortable-id="720p"] input[aria-label="转码优先级"]')
    expect(priority.attributes()).toMatchObject({ type: 'number', min: '-2147483648', max: '2147483647', step: '1' })
    await priority.setValue('100')
    await wrapper.findAll('button').find(button => button.text() === '添加清晰度')!.trigger('click')
    const added = wrapper.findAll('[data-sortable-id]')[2]!
    const addedId = added.attributes('data-sortable-id')!
    expect(added.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '0')
    await added.get('input[aria-label="转码优先级"]').setValue('-10')
    await added.get('.reorder-handle').trigger('keydown', { key: 'Home' })
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(current.transcoding).toEqual(original)

    await save(wrapper)

    expect(current.transcoding!.qualityOrder).toEqual([addedId, 'original', '720p'])
    expect(current.transcoding!.defaultPlaybackQualityId).toBe('720p')
    expect(current.transcoding!.profiles.find(profile => profile.id === '720p')).toHaveProperty('priority', 100)
    expect(current.transcoding!.profiles.find(profile => profile.id === addedId)).toHaveProperty('priority', -10)
    const reopened = await openTranscoding()
    expect(reopened.get('[data-sortable-id="720p"] input[aria-label="转码优先级"]').element).toHaveProperty('value', '100')
    expect(reopened.get(`[data-sortable-id="${addedId}"] input[aria-label="转码优先级"]`).element).toHaveProperty('value', '-10')
  })

  it.each([-2147483648, -1, 0, 100, 2147483647])('转码优先级 %s 可保存并重新读取', async priority =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="转码优先级"]').setValue(String(priority))
    await save(wrapper)

    expect(current.transcoding!.profiles[0]).toHaveProperty('priority', priority)
    const reopened = await openTranscoding()
    expect(reopened.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', String(priority))
  })

  it.each(['-2147483649', '2147483648', '1.5', ''])('转码优先级 %s 无效时阻止保存并保留输入', async value =>
  {
    const wrapper = await openTranscoding()
    const priority = wrapper.get('input[aria-label="转码优先级"]')
    await priority.setValue(value)
    await save(wrapper)

    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(priority.element).toHaveProperty('value', value)
    expect(priority.attributes('data-error')).toContain('整数')
    expect(wrapper.get('[role="alert"]').text()).toContain('转码优先级')
  })

  it('优先级保存失败保留草稿，重试成功后重新读取保留数值', async () =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="转码优先级"]').setValue('100')
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('保存失败'))

    await save(wrapper)

    expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
    expect(wrapper.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '100')
    expect(current.transcoding).toBeUndefined()
    await save(wrapper)
    expect(current.transcoding!.profiles[0]).toHaveProperty('priority', 100)
    const reopened = await openTranscoding()
    expect(reopened.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '100')
  })

  it('全部关闭自动转码可保存，保存失败保留草稿，重新读取保留显式关闭', async () =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="自动转码"]').setValue(false)
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('保存失败'))

    await save(wrapper)

    expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
    expect(wrapper.get('input[aria-label="自动转码"]').element).toHaveProperty('checked', false)
    await save(wrapper)
    expect(current.transcoding!.profiles[0]).toMatchObject({ enabled: true, autoTranscode: false })
    expect(current.transcoding!.defaultPlaybackQualityId).toBe('720p')
    const reopened = await openTranscoding()
    expect(reopened.get('input[aria-label="自动转码"]').element).toHaveProperty('checked', false)
  })

  it('停用档位时禁用自动转码控件，保存和重新启用后保留自动偏好', async () =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('[data-sortable-id="720p"] input[aria-label="启用"]').setValue(false)
    const automatic = wrapper.get('input[aria-label="自动转码"]')
    expect(automatic.attributes('disabled')).toBeDefined()
    expect(automatic.element).toHaveProperty('checked', true)

    await save(wrapper)

    expect(current.transcoding!.profiles[0]).toMatchObject({ enabled: false, autoTranscode: true })
    const reopened = await openTranscoding()
    expect(reopened.get('input[aria-label="自动转码"]').attributes('disabled')).toBeDefined()
    await reopened.get('[data-sortable-id="720p"] input[aria-label="启用"]').setValue(true)
    expect(reopened.get('input[aria-label="自动转码"]').attributes('disabled')).toBeUndefined()
    expect(reopened.get('input[aria-label="自动转码"]').element).toHaveProperty('checked', true)
  })

  it('保存失败保留排序与编辑，保存期间禁用输入和拖拽', async () =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get('input[aria-label="名称"]').setValue('手机省流')
    await wrapper.get('input[aria-label="转码优先级"]').setValue('10')
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('保存失败'))
    await save(wrapper)
    expect(wrapper.get('input[aria-label="名称"]').element).toHaveProperty('value', '手机省流')
    expect(wrapper.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '10')
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

  it.each([
    ['zh-CN', '分辨率短边（px）', '分片时长（秒）', '关键帧间隔（秒）', '单文件存储（HLS 字节范围播放）', '自动转码', '转码优先级'],
    ['en-US', 'Resolution short edge (px)', 'Segment duration (seconds)', 'Keyframe interval (seconds)', 'Single-file storage (HLS byte-range playback)', 'Auto transcode', 'Transcoding priority'],
    ['ja-JP', '解像度の短辺（px）', 'セグメント長（秒）', 'キーフレーム間隔（秒）', '単一ファイル保存（HLSバイト範囲再生）', '自動変換', '変換の優先度'],
  ] as const)('%s 包含清晰度、分片、自动转码和优先级控件文案', async (locale, label, segment, keyframe, singleFile, automatic, priority) =>
  {
    const wrapper = await openTranscoding()
    i18n.global.locale.value = locale
    await nextTick()
    expect(wrapper.find(`input[aria-label="${label}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${segment}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${keyframe}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${singleFile}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${automatic}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${priority}"]`).exists()).toBe(true)
    expect(wrapper.text()).toContain(i18n.global.t('transcoding.priorityHint'))
    expect(wrapper.text()).not.toContain('transcoding.')
  })

  it('旧档位缺少分片、自动转码和优先级字段时补默认值，保存后写入完整配置且不提前修改原数据', async () =>
  {
    current.transcoding = JSON.parse(JSON.stringify({
      enabled: true,
      profiles: [{ id: 'legacy', name: '旧清晰度', height: 720, videoBitrateKbps: 2500, maxFrameRate: null, audioBitrateKbps: 128, enabled: true }],
      qualityOrder: ['original', 'legacy'], defaultPlaybackQualityId: 'legacy',
    }))
    const original = structuredClone(current.transcoding!)
    const normalized = normalizeTranscodingSettings(current.transcoding)
    expect(normalized.profiles[0]).toMatchObject({ segmentDurationSeconds: 6, keyframeIntervalSeconds: 2, singleFile: false, autoTranscode: true, priority: 0 })
    expect(current.transcoding).toEqual(original)
    const wrapper = await openTranscoding()
    expect(wrapper.get('input[aria-label="分片时长（秒）"]').element).toHaveProperty('value', '6')
    expect(wrapper.get('input[aria-label="关键帧间隔（秒）"]').element).toHaveProperty('value', '2')
    expect(wrapper.get('input[aria-label="单文件存储（HLS 字节范围播放）"]').element).toHaveProperty('checked', false)
    expect(wrapper.get('input[aria-label="自动转码"]').element).toHaveProperty('checked', true)
    expect(wrapper.get('input[aria-label="转码优先级"]').element).toHaveProperty('value', '0')
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(current.transcoding!.profiles[0]).toMatchObject({ id: 'legacy', segmentDurationSeconds: 6, keyframeIntervalSeconds: 2, singleFile: false, autoTranscode: true, priority: 0 })
  })

  it('每档位独立保存小数分片参数和单文件模式，重新读取保留完整值', async () =>
  {
    const wrapper = await openTranscoding()
    const segment = wrapper.get('input[aria-label="分片时长（秒）"]')
    expect(segment.attributes()).toMatchObject({ min: '0.1', max: '300', step: 'any' })
    await segment.setValue('7.125')
    await wrapper.get('input[aria-label="关键帧间隔（秒）"]').setValue('1.75')
    await wrapper.get('input[aria-label="单文件存储（HLS 字节范围播放）"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text() === '添加清晰度')!.trigger('click')
    expect(wrapper.findAll('input[aria-label="分片时长（秒）"]')[1]!.element).toHaveProperty('value', '6')
    expect(current.transcoding).toBeUndefined()
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(current.transcoding!.profiles[0]).toMatchObject({ segmentDurationSeconds: 7.125, keyframeIntervalSeconds: 1.75, singleFile: true })
    expect(current.transcoding!.profiles[1]).toMatchObject({ segmentDurationSeconds: 6, keyframeIntervalSeconds: 2, singleFile: false })
    const reopened = await openTranscoding()
    expect(reopened.findAll('input[aria-label="分片时长（秒）"]').map(input => (input.element as HTMLInputElement).value)).toEqual(['7.125', '6'])
    expect(reopened.findAll('input[aria-label="单文件存储（HLS 字节范围播放）"]').map(input => (input.element as HTMLInputElement).checked)).toEqual([true, false])
  })

  it.each([
    ['分片时长（秒）', '0.09'], ['分片时长（秒）', '300.01'], ['分片时长（秒）', ''],
    ['关键帧间隔（秒）', '0.09'], ['关键帧间隔（秒）', '300.01'], ['关键帧间隔（秒）', ''],
  ])('拒绝无效的 %s 值 %s 并保留草稿', async (label, value) =>
  {
    const wrapper = await openTranscoding()
    await wrapper.get(`input[aria-label="${label}"]`).setValue(value)
    await save(wrapper)
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(wrapper.get(`input[aria-label="${label}"]`).element).toHaveProperty('value', value)
    expect(wrapper.get('[role="alert"]').text()).toContain('请检查清晰度设置')
  })

  it.each([[.1, 300], [300, .1], [6.125, 2.375]])('分片时长 %s 与关键帧间隔 %s 可独立设置，边界及小数合法', (segmentDurationSeconds, keyframeIntervalSeconds) =>
  {
    const settings = defaultTranscodingSettings()
    Object.assign(settings.profiles[0]!, { segmentDurationSeconds, keyframeIntervalSeconds, singleFile: true })
    const normalized = normalizeTranscodingSettings(JSON.parse(JSON.stringify(settings)))
    expect(normalized).toEqual(settings)
    expect(isValidTranscodingSettings(normalized)).toBe(true)
  })

  it.each(['segmentDurationSeconds', 'keyframeIntervalSeconds'] as const)('%s 拒绝非有限值和范围外数值', field =>
  {
    for (const invalid of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, -.1, 0, .099, 300.001])
    {
      const settings = defaultTranscodingSettings()
      settings.profiles[0]![field] = invalid
      expect(isValidTranscodingSettings(settings)).toBe(false)
    }
  })

  it('单文件模式要求布尔值，字符串配置不能通过校验', () =>
  {
    const settings = JSON.parse(JSON.stringify(defaultTranscodingSettings()))
    settings.profiles[0].singleFile = 'true'
    expect(isValidTranscodingSettings(settings)).toBe(false)
  })

  it.each([null, '', '100', true, Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, 1.5, -2147483649, 2147483648])('显式非法优先级 %s 不能在归一化时被修正或通过校验', priority =>
  {
    const settings = JSON.parse(JSON.stringify(defaultTranscodingSettings()))
    settings.profiles[0].priority = priority
    const normalized = normalizeTranscodingSettings(settings)

    expect(normalized.profiles[0]).toHaveProperty('priority', priority)
    expect(isValidTranscodingSettings(normalized)).toBe(false)
  })

  it.each(['false', 'true', 0, 1])('自动转码开关拒绝非布尔配置 %s', value =>
  {
    const settings = JSON.parse(JSON.stringify(defaultTranscodingSettings()))
    settings.profiles[0].autoTranscode = value
    expect(isValidTranscodingSettings(settings)).toBe(false)
  })

  it.each([false, true])('归一化与校验保留显式自动转码配置 %s', automatic =>
  {
    const settings = JSON.parse(JSON.stringify(defaultTranscodingSettings()))
    settings.profiles[0].autoTranscode = automatic
    const normalized = normalizeTranscodingSettings(settings)
    expect(normalized.profiles[0]).toHaveProperty('autoTranscode', automatic)
    expect(isValidTranscodingSettings(normalized)).toBe(true)
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

  it('切片节点校验共用参数并独立于转码设备能力', () =>
  {
    const worker = defaultWorkerConfiguration()
    worker.gpuConcurrency = worker.cpuConcurrency = 0
    worker.cpuSampleWindowSeconds = 0
    expect(isValidWorkerConfiguration(worker, 'Clipping')).toBe(true)
    worker.maxConcurrentEncodes = 0
    expect(isValidWorkerConfiguration(worker, 'Clipping')).toBe(false)
    worker.maxConcurrentEncodes = 2
    worker.downloadBytesPerSecond = Number.NaN
    expect(isValidWorkerConfiguration(worker, 'Clipping')).toBe(false)
  })
})
