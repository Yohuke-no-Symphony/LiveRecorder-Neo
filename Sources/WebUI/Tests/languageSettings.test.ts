// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import { usePreferencesStore } from '../src/stores/preferences'
import type { UserProfile } from '../src/models/auth'
import type { ServerPaths, ServerSettings } from '../src/models/settings'
import type { AppLanguage } from '../src/localization/types'
import AccountView from '../src/views/AccountView.vue'
import SettingsView from '../src/views/SettingsView.vue'
import AppBrand from '../src/components/common/AppBrand.vue'

const container = { template: '<div><slot /></div>' }
const select = defineComponent({
  props: ['modelValue', 'items', 'label', 'disabled', 'loading'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('select', {
      value: props.modelValue ?? '', 'aria-label': props.label, disabled: props.disabled || props.loading,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value || null),
    }, (props.items as { title: string; value: string | null }[]).map(item => h('option', { value: item.value ?? '' }, item.title)))])
  },
})
const button = defineComponent({
  props: ['type', 'disabled', 'loading'],
  setup(props, { slots })
  {
    return () => h('button', { type: props.type ?? 'button', disabled: props.disabled || props.loading }, slots.default?.())
  },
})
const snackbar = defineComponent({
  props: ['modelValue'],
  setup(props, { slots })
  {
    return () => props.modelValue ? h('div', { role: 'status' }, slots.default?.()) : null
  },
})
const tabs = defineComponent({
  props: ['modelValue'],
  emits: ['update:modelValue'],
  setup(_, { slots })
  {
    return () => h('div', slots.default?.())
  },
})
const textField = defineComponent({
  props: ['modelValue', 'label', 'hint', 'readonly', 'disabled', 'type', 'min', 'max', 'step'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      value: props.modelValue, 'aria-label': props.label, readonly: props.readonly, disabled: props.disabled,
      type: props.type, min: props.min, max: props.max, step: props.step,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    }), props.hint])
  },
})
const stubs = {
  VSelect: select, VBtn: button, VCard: container, VList: container, VListItem: container,
  VTabs: tabs, VTab: container, VSnackbar: snackbar, VTextField: textField, VSwitch: true,
  VAlert: { template: '<div role="alert"><slot /></div>' }, VProgressLinear: true, VIcon: true, VDivider: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) =>
  {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

function user(language: AppLanguage | null): UserProfile
{
  return { id: 'user-1', email: 'user@example.test', displayName: '原始用户昵称', role: 'Admin', status: 'Active', permissions: [], language }
}

function settings(defaultLanguage: AppLanguage): ServerSettings
{
  return {
    id: 'server', defaultLanguage, translationDanmuPattern: '.*?【.*?】', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, recordingRetentionDays: 0, clipRetentionDays: 7, trashRetentionDays: 7, taskLogRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    streamDisconnectTimeoutMinutes: 5,
    defaultUserPermissions: [],
    registrationEnabled: true, ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe',
    registrationApprovalRequired: true, registrationInvitationRequired: true, registrationEmailVerificationRequired: true,
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
    oauthProviders: [],
  }
}
const paths: ServerPaths = {
  dataDirectory: '/srv/Data', recordsDirectory: '/srv/Data/records', cacheDirectory: '/srv/Data/cache',
  configFile: '/srv/settings.json', configuredDataDirectory: 'Data', configuredRecordsDirectory: 'Data/records', restartRequired: false,
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
  vi.spyOn(AuthAPI, 'identities').mockResolvedValue([])
  vi.spyOn(MediaAPI, 'settingsPaths').mockResolvedValue(paths)
  vi.spyOn(MediaAPI, 'updateSettingsPaths').mockResolvedValue(paths)
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  i18n.global.locale.value = 'zh-CN'
  vi.unstubAllGlobals()
})

describe('账户语言设置', () =>
{
  it('仅在服务器确认保存后更新账户、选择器和生效语言', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user('zh-CN')
    const pending = deferred<UserProfile>()
    const save = vi.spyOn(AuthAPI, 'setLanguage').mockReturnValue(pending.promise)
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('ja-JP')
    expect(save).toHaveBeenCalledExactlyOnceWith('ja-JP')
    expect(auth.user?.language).toBe('zh-CN')
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')
    expect(wrapper.get('select').element.value).toBe('zh-CN')
    expect(wrapper.get('select').attributes('disabled')).toBeDefined()

    pending.resolve(user('ja-JP'))
    await flushPromises()
    expect(auth.user?.language).toBe('ja-JP')
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    expect(wrapper.get('select').element.value).toBe('ja-JP')
    expect(wrapper.get('select').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[role="status"]').text()).toBe('言語設定を保存しました')
  })

  it('保存失败时保留旧语言并允许重试', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user('zh-CN')
    const pending = deferred<UserProfile>()
    vi.spyOn(AuthAPI, 'setLanguage').mockReturnValue(pending.promise)
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('en-US')
    pending.reject(new Error('语言服务不可用'))
    await flushPromises()
    expect(auth.user?.language).toBe('zh-CN')
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')
    expect(wrapper.get('select').element.value).toBe('zh-CN')
    expect(wrapper.get('select').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[role="alert"]').text()).toBe('语言服务不可用')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('将跟随全局保存为 null，并立即采用管理员的默认语言', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('en-US')
    auth.user = user('zh-CN')
    const save = vi.spyOn(AuthAPI, 'setLanguage').mockResolvedValue(user(null))
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('')
    await flushPromises()
    expect(save).toHaveBeenCalledExactlyOnceWith(null)
    expect(auth.user?.language).toBeNull()
    expect(preferences.effectiveLanguage).toBe('en-US')
    expect(i18n.global.locale.value).toBe('en-US')
    expect(wrapper.get('select').element.value).toBe('')
  })
})

describe('管理员默认语言', () =>
{
  it('常规页编辑配置目录并展示当前目录，录制页不提供目录配置，全局设置不提交路径', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockResolvedValue(settings('zh-CN'))
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(MediaAPI.settingsPaths).toHaveBeenCalledOnce()
    expect(wrapper.findComponent(tabs).props('modelValue')).toBe('general')
    const dataDirectory = wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]')
    expect(dataDirectory.element.value).toBe('Data')
    expect(dataDirectory.element.readOnly).toBe(false)
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.readOnly).toBe(true)
    expect(wrapper.get<HTMLInputElement>('input[aria-label="配置文件"]').element.value).toBe('/srv/settings.json')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('Data/records')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.readOnly).toBe(false)
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.readOnly).toBe(true)
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'recording')
    await nextTick()
    expect(wrapper.find('input[aria-label="数据目录"]').exists()).toBe(false)
    expect(wrapper.find('input[aria-label="录像目录"]').exists()).toBe(false)
    expect(wrapper.find('input[aria-label="录播目录"]').exists()).toBe(false)
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledOnce()
    const submitted = update.mock.calls[0]![0]
    expect(submitted).not.toHaveProperty('storagePath')
    expect(submitted).not.toHaveProperty('dataDirectory')
    expect(submitted).not.toHaveProperty('recordsDirectory')
    expect(MediaAPI.updateSettingsPaths).not.toHaveBeenCalled()
  })

  it('单独保存数据与录像目录，保留当前运行目录并根据服务器返回提示重启，其他无效设置不影响保存', async () =>
  {
    const invalidSettings = settings('zh-CN')
    invalidSettings.segmentBytes = 0
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(invalidSettings)
    const globalSave = vi.spyOn(MediaAPI, 'updateSettings')
    const pending = deferred<ServerPaths>()
    vi.mocked(MediaAPI.updateSettingsPaths).mockReturnValue(pending.promise)
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await wrapper.get('input[aria-label="数据目录"]').setValue('../MovedData')
    await wrapper.get('input[aria-label="录像目录"]').setValue('/mnt/MovedRecords')
    const save = wrapper.findAll('button').find(item => item.text() === '保存目录配置')!
    expect(save.attributes('type')).toBe('submit')
    await wrapper.get('form').trigger('submit')
    expect(MediaAPI.updateSettingsPaths).toHaveBeenCalledExactlyOnceWith({ dataDirectory: '../MovedData', recordsDirectory: '/mnt/MovedRecords' })
    expect(globalSave).not.toHaveBeenCalled()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.text()).not.toContain('目录配置已保存')
    pending.resolve({ ...paths, configuredDataDirectory: '../MovedData', configuredRecordsDirectory: '/mnt/MovedRecords', restartRequired: true })
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('../MovedData')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('/mnt/MovedRecords')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.text()).toContain('目录配置已保存')
    expect(wrapper.text()).toContain('目录变更待重启后生效')
    expect(globalSave).not.toHaveBeenCalled()
  })

  it('重新读取服务器配置后保留待重启提示，不把已配置目录当成当前目录', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    vi.mocked(MediaAPI.settingsPaths).mockResolvedValue({ ...paths, configuredDataDirectory: '../MovedData', configuredRecordsDirectory: '/mnt/MovedRecords', restartRequired: true })
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('../MovedData')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('/mnt/MovedRecords')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.text()).toContain('目录变更待重启后生效')
    expect(wrapper.text()).not.toContain('目录配置已保存')
  })

  it('数据目录保存失败保留编辑值和当前目录，不显示成功', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    vi.mocked(MediaAPI.updateSettingsPaths).mockRejectedValue(new Error('配置文件不可写'))
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await wrapper.get('input[aria-label="数据目录"]').setValue('/srv/NextData')
    await wrapper.get('input[aria-label="录像目录"]').setValue('/mnt/NextRecords')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('/srv/NextData')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('/mnt/NextRecords')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.text()).toContain('配置文件不可写')
    expect(wrapper.text()).not.toContain('目录配置已保存')
    expect(wrapper.text()).not.toContain('目录变更待重启后生效')
  })

  it('可以只修改录像目录，不会派生或改写数据目录', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    vi.mocked(MediaAPI.updateSettingsPaths).mockResolvedValue({ ...paths, configuredRecordsDirectory: '../SeparateRecords', restartRequired: true })
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await wrapper.get('input[aria-label="录像目录"]').setValue('../SeparateRecords')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('Data')
    const save = wrapper.findAll('button').find(item => item.text() === '保存目录配置')!
    expect(save.attributes('disabled')).toBeUndefined()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(MediaAPI.updateSettingsPaths).toHaveBeenCalledExactlyOnceWith({ dataDirectory: 'Data', recordsDirectory: '../SeparateRecords' })
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前数据目录"]').element.value).toBe('/srv/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="当前录像目录"]').element.value).toBe('/srv/Data/records')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('Data')
  })

  it('通过保存全局设置同步 preferences，并更新跟随全局的账户', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user(null)
    vi.spyOn(MediaAPI, 'settings').mockResolvedValueOnce(settings('zh-CN')).mockResolvedValueOnce(settings('ja-JP'))
    const pending = deferred<ServerSettings>()
    const save = vi.spyOn(MediaAPI, 'updateSettings').mockReturnValue(pending.promise)
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    expect([...wrapper.get('select').element.options].map(option => option.value)).toEqual(['zh-CN', 'en-US', 'ja-JP'])
    await wrapper.get('select').setValue('ja-JP')
    expect(save).not.toHaveBeenCalled()
    expect(preferences.defaultLanguage).toBe('zh-CN')
    await wrapper.get('header button').trigger('click')
    expect(save).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ defaultLanguage: 'ja-JP' }))
    expect(preferences.defaultLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')

    pending.resolve(settings('ja-JP'))
    await flushPromises()
    expect(preferences.defaultLanguage).toBe('ja-JP')
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    expect(wrapper.get('h1').text()).toBe('設定')
    expect(wrapper.get('[role="status"]').text()).toBe('設定を保存しました')
  })
})

describe('录制断流超时', () =>
{
  async function openRecordingSettings(): Promise<VueWrapper>
  {
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'recording')
    await nextTick()
    return wrapper
  }

  it.each([1, 17, 1440])('保存 %s 分钟并在重新加载后显示已保存的值', async minutes =>
  {
    let current = settings('zh-CN')
    vi.spyOn(MediaAPI, 'settings').mockImplementation(async () => structuredClone(current))
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockImplementation(async value =>
    {
      current = JSON.parse(JSON.stringify(value)) as ServerSettings
      return current
    })
    const wrapper = await openRecordingSettings()
    const input = wrapper.get<HTMLInputElement>('input[aria-label="断流后结束录制（分钟）"]')
    expect(input.element.value).toBe('5')
    expect(input.attributes()).toMatchObject({ min: '1', max: '1440', step: '1' })

    await input.setValue(String(minutes))
    await wrapper.get('header button').trigger('click')
    await flushPromises()

    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ streamDisconnectTimeoutMinutes: minutes }))
    expect(wrapper.get<HTMLInputElement>('input[aria-label="断流后结束录制（分钟）"]').element.value).toBe(String(minutes))
    expect(wrapper.get('[role="status"]').text()).toBe('设置已保存')
    const reopened = await openRecordingSettings()
    expect(reopened.get<HTMLInputElement>('input[aria-label="断流后结束录制（分钟）"]').element.value).toBe(String(minutes))
  })

  it.each(['', '0', '-1', '1441', '1.5'])('拒绝无效时长 %s 并保留待编辑值', async value =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    const update = vi.spyOn(MediaAPI, 'updateSettings')
    const wrapper = await openRecordingSettings()
    await wrapper.get('input[aria-label="断流后结束录制（分钟）"]').setValue(value)
    await wrapper.get('header button').trigger('click')
    await flushPromises()

    expect(update).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toBe('断流结束时长必须为 1 至 1440 分钟的整数')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.get<HTMLInputElement>('input[aria-label="断流后结束录制（分钟）"]').element.value).toBe(value)
  })

  it.each([
    ['zh-CN', '断流后结束录制（分钟）', '恢复后开启新场次'],
    ['en-US', 'End recording after stream loss (minutes)', 'starts a new session'],
    ['ja-JP', '配信切断後に録画を終了するまで（分）', '新しい録画セッション'],
  ] as const)('%s 显示断流时长和恢复行为说明', async (language, label, explanation) =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('zh-CN'))
    const wrapper = await openRecordingSettings()
    i18n.global.locale.value = language
    await nextTick()

    expect(wrapper.get<HTMLInputElement>(`input[aria-label="${label}"]`).element.value).toBe('5')
    expect(wrapper.text()).toContain(explanation)
  })
})

describe('网站品牌', () =>
{
  it('保持同一角色 LOGO，实时显示所选语言的网站名', async () =>
  {
    const wrapper = mount(AppBrand, { global: { plugins: [i18n] } })
    mounted.push(wrapper)
    const logo = wrapper.get('img').attributes('src')
    for (const [language, name] of [['zh-CN', '夜半录播'], ['en-US', 'Yohuke Live Recorder'], ['ja-JP', '夜更けのレコーダー']] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.text()).toBe(name)
      expect(wrapper.get('img').attributes('src')).toBe(logo)
    }
  })
})
