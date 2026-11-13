// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h } from 'vue'
import SetupView from '../src/views/SetupView.vue'
import router from '../src/router'
import { setCsrfToken } from '../src/apis/APIBase'
import { i18n } from '../src/plugins/i18n'
import { AuthAPI } from '../src/apis/AuthAPI'
import { useAuthStore } from '../src/stores/auth'
import { MediaAPI } from '../src/apis/MediaAPI'

const textField = defineComponent({
  props: ['modelValue', 'label', 'type', 'readonly'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      value: props.modelValue ?? '', type: props.type ?? 'text', 'aria-label': props.label, readonly: props.readonly,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    })])
  },
})
const select = defineComponent({
  props: ['modelValue', 'label', 'items'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('select', {
      value: props.modelValue, 'aria-label': props.label,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value),
    }, (props.items as { title: string; value: string }[]).map(item => h('option', { value: item.value }, item.title)))])
  },
})
const button = defineComponent({
  props: ['type', 'disabled', 'loading'],
  setup(props, { slots })
  {
    return () => h('button', { type: props.type ?? 'button', disabled: props.disabled || props.loading }, slots.default?.())
  },
})
let wrapper: VueWrapper | undefined
const stubs = {
  VTextField: textField, VSelect: select, VBtn: button,
  VCard: { template: '<div><slot /></div>' },
  VChip: { template: '<span><slot /></span>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VIcon: true, VSpacer: true,
}

beforeEach(() =>
{
  vi.stubEnv('DEV', false)
  i18n.global.locale.value = 'zh-CN'
})

afterEach(() =>
{
  wrapper?.unmount()
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
  setCsrfToken('')
})

describe('首次安装页面', () =>
{
  it('从未安装入口取得匿名 CSRF，再通过实际三步表单提交安装合同', async () =>
  {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const calls: { path: string; options: RequestInit }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, options: RequestInit) =>
    {
      expect(new URL(url).origin).toBe(window.location.origin)
      const path = new URL(url).pathname
      calls.push({ path, options })
      if (path === '/api/setup/status')
      {
        return new Response(JSON.stringify({ installed: false, requiresToken: true, dataDirectory: '/srv/Data', recordsDirectory: '/srv/Data/records' }))
      }
      if (path === '/api/auth/me')
      {
        return new Response(JSON.stringify({ user: null, csrfToken: 'anonymous-install-csrf' }))
      }
      if (path === '/api/ui-settings')
      {
        return new Response(JSON.stringify({ defaultLanguage: 'zh-CN' }))
      }
      if (path === '/api/setup')
      {
        const valid = new Headers(options.headers).get('X-CSRF-TOKEN') === 'anonymous-install-csrf'
        return new Response(JSON.stringify({ detail: valid ? '数据库不可连接' : 'CSRF 令牌缺失' }), { status: valid ? 422 : 400 })
      }
      throw new Error(`未预期的请求：${path}`)
    }))
    const pinia = createPinia()
    setActivePinia(pinia)
    await router.push('/setup')
    await router.isReady()
    wrapper = mount(SetupView, {
      global: {
        plugins: [pinia, router, i18n],
        stubs,
      },
    })
    await wrapper.get('input[aria-label="安装令牌"]').setValue('test-install-token')
    await wrapper.get('input[aria-label="管理员名称"]').setValue('测试管理员')
    await wrapper.get('input[aria-label="管理员邮箱"]').setValue('admin@example.test')
    await wrapper.get('input[aria-label="密码"]').setValue('test-password-123456')
    await wrapper.get('input[aria-label="确认密码"]').setValue('test-password-123456')
    await wrapper.get('form').trigger('submit')
    const dataDirectory = wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]')
    const recordsDirectory = wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]')
    expect(dataDirectory.element.value).toBe('/srv/Data')
    expect(recordsDirectory.element.value).toBe('/srv/Data/records')
    expect(dataDirectory.element.readOnly).toBe(false)
    expect(recordsDirectory.element.readOnly).toBe(false)
    await dataDirectory.setValue('/srv/SelectedData')
    expect(recordsDirectory.element.value).toBe('/srv/Data/records')
    await recordsDirectory.setValue('/mnt/SelectedRecords')
    expect(dataDirectory.element.value).toBe('/srv/SelectedData')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('input[aria-label="数据库名称"]').setValue('recorder-install-test')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    await vi.waitFor(() => expect(wrapper!.get('[role="alert"]').text()).toBe('数据库不可连接'))

    expect(calls.map(call => call.path)).toEqual(['/api/setup/status', '/api/ui-settings', '/api/auth/me', '/api/setup'])
    const submitted = calls.at(-1)!.options
    expect(submitted.method).toBe('POST')
    expect(submitted.credentials).toBe('include')
    expect(new Headers(submitted.headers).get('X-CSRF-TOKEN')).toBe('anonymous-install-csrf')
    expect(JSON.parse(String(submitted.body))).toEqual({
      token: 'test-install-token', userName: '测试管理员', email: 'admin@example.test', password: 'test-password-123456',
      mode: 'New', mongoConnectionString: 'mongodb://127.0.0.1:27017', databaseName: 'recorder-install-test',
      dataDirectory: '/srv/SelectedData', recordsDirectory: '/mnt/SelectedRecords',
      ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe',
    })
    await wrapper.get('button[type="button"]').trigger('click')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('/srv/SelectedData')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('/mnt/SelectedRecords')
    expect(useAuthStore().dataDirectory).toBe('/srv/Data')
    expect(useAuthStore().recordsDirectory).toBe('/srv/Data/records')
  })

  it('恢复实例使用原管理员凭据，切换模式不会用新安装的媒体工具默认值覆盖旧配置', async () =>
  {
    const pinia = createPinia()
    setActivePinia(pinia)
    const auth = useAuthStore()
    auth.dataDirectory = '/srv/RestoredData'
    auth.recordsDirectory = '/srv/RestoredData/records'
    const setup = vi.spyOn(AuthAPI, 'setup').mockRejectedValue(new Error('恢复校验失败'))
    wrapper = mount(SetupView, { global: { plugins: [pinia, i18n], stubs } })
    await wrapper.get('input[aria-label="安装令牌"]').setValue('restore-token')
    await wrapper.get('input[aria-label="管理员名称"]').setValue('不能覆盖原昵称')
    await wrapper.get('select[aria-label="安装方式"]').setValue('Restore')
    expect(wrapper.find('input[aria-label="管理员名称"]').exists()).toBe(false)
    expect(wrapper.find('input[aria-label="确认密码"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('原管理员')
    await wrapper.get('input[aria-label="管理员邮箱"]').setValue('original-admin@example.test')
    await wrapper.get('input[aria-label="密码"]').setValue('original-password')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('input[aria-label="数据目录"]').setValue('/restore/Data')
    await wrapper.get('input[aria-label="录像目录"]').setValue('/archive/Records')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="FFmpeg"]').element.value).toBe('')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="FFprobe"]').element.value).toBe('')
    await wrapper.get('button[type="button"]').trigger('click')
    await wrapper.get('select[aria-label="安装方式"]').setValue('New')
    await wrapper.get('input[aria-label="确认密码"]').setValue('original-password')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="FFmpeg"]').element.value).toBe('ffmpeg')
    await wrapper.get('input[aria-label="FFmpeg"]').setValue('/custom/ffmpeg')
    await wrapper.get('button[type="button"]').trigger('click')
    await wrapper.get('select[aria-label="安装方式"]').setValue('Restore')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="FFmpeg"]').element.value).toBe('')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('input[aria-label="数据库名称"]').setValue('existing-recorder')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(setup).toHaveBeenCalledExactlyOnceWith({
      mode: 'Restore', token: 'restore-token', userName: '', email: 'original-admin@example.test', password: 'original-password',
      mongoConnectionString: 'mongodb://127.0.0.1:27017', databaseName: 'existing-recorder', ffmpegPath: '', ffprobePath: '',
      dataDirectory: '/restore/Data', recordsDirectory: '/archive/Records',
    })
    expect(wrapper.get('[role="alert"]').text()).toBe('恢复校验失败')
    await wrapper.get('button[type="button"]').trigger('click')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe('/restore/Data')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe('/archive/Records')
  })

  it('Windows 的两个路径独立保留，安装成功直接进入登录而不预先保存路径或要求重启', async () =>
  {
    const pinia = createPinia()
    setActivePinia(pinia)
    const auth = useAuthStore()
    auth.dataDirectory = String.raw`C:\Recorder\Data`
    auth.recordsDirectory = String.raw`C:\Recorder\Data\records`
    const setup = vi.spyOn(AuthAPI, 'setup').mockResolvedValue(undefined)
    const savePaths = vi.spyOn(MediaAPI, 'updateSettingsPaths')
    wrapper = mount(SetupView, { global: { plugins: [pinia, i18n], stubs } })
    await wrapper.get('input[aria-label="安装令牌"]').setValue('windows-install-token')
    await wrapper.get('input[aria-label="管理员名称"]').setValue('Windows 管理员')
    await wrapper.get('input[aria-label="管理员邮箱"]').setValue('windows-admin@example.test')
    await wrapper.get('input[aria-label="密码"]').setValue('windows-admin-password')
    await wrapper.get('input[aria-label="确认密码"]').setValue('windows-admin-password')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="数据目录"]').element.value).toBe(String.raw`C:\Recorder\Data`)
    await wrapper.get('input[aria-label="数据目录"]').setValue(String.raw`D:\PrivateData`)
    await wrapper.get('input[aria-label="录像目录"]').setValue(String.raw`\\nas\recordings`)
    await wrapper.get('input[aria-label="数据目录"]').setValue(String.raw`D:\SelectedData`)
    expect(wrapper.get<HTMLInputElement>('input[aria-label="录像目录"]').element.value).toBe(String.raw`\\nas\recordings`)
    expect(savePaths).not.toHaveBeenCalled()
    await wrapper.get('form').trigger('submit')
    const assign = vi.fn()
    const browserWindow = window
    vi.stubGlobal('window', new Proxy(browserWindow, {
      get(target, key)
      {
        return key === 'location' ? { assign } : Reflect.get(target, key, target)
      },
    }))
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(setup).toHaveBeenCalledExactlyOnceWith({
      mode: 'New', token: 'windows-install-token', userName: 'Windows 管理员', email: 'windows-admin@example.test', password: 'windows-admin-password',
      dataDirectory: String.raw`D:\SelectedData`, recordsDirectory: String.raw`\\nas\recordings`,
      mongoConnectionString: 'mongodb://127.0.0.1:27017', databaseName: 'yohuke-live-recorder', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe',
    })
    expect(assign).toHaveBeenCalledExactlyOnceWith('/login')
    expect(savePaths).not.toHaveBeenCalled()
    expect(wrapper.text()).not.toContain('重启后生效')
  })
})
