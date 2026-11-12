// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h } from 'vue'
import SetupView from '../src/views/SetupView.vue'
import router from '../src/router'
import { setCsrfToken } from '../src/apis/APIBase'

const textField = defineComponent({
  props: ['modelValue', 'label', 'type'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      value: props.modelValue ?? '', type: props.type ?? 'text', 'aria-label': props.label,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    })])
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

beforeEach(() =>
{
  vi.stubEnv('DEV', false)
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
        return new Response(JSON.stringify({ installed: false, requiresToken: true }))
      }
      if (path === '/api/auth/me')
      {
        return new Response(JSON.stringify({ user: null, csrfToken: 'anonymous-install-csrf' }))
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
        plugins: [pinia, router],
        stubs: {
          VTextField: textField, VBtn: button,
          VCard: { template: '<div><slot /></div>' },
          VChip: { template: '<span><slot /></span>' },
          VAlert: { template: '<div role="alert"><slot /></div>' },
          VIcon: true, VSpacer: true,
        },
      },
    })
    await wrapper.get('input[aria-label="安装令牌"]').setValue('test-install-token')
    await wrapper.get('input[aria-label="管理员名称"]').setValue('测试管理员')
    await wrapper.get('input[aria-label="管理员邮箱"]').setValue('admin@example.test')
    await wrapper.get('input[aria-label="密码"]').setValue('test-password-123456')
    await wrapper.get('input[aria-label="确认密码"]').setValue('test-password-123456')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('input[aria-label="录播目录"]').setValue('/srv/recordings')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('input[aria-label="数据库名称"]').setValue('recorder-install-test')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    await vi.waitFor(() => expect(wrapper!.get('[role="alert"]').text()).toBe('数据库不可连接'))

    expect(calls.map(call => call.path)).toEqual(['/api/setup/status', '/api/auth/me', '/api/setup'])
    const submitted = calls.at(-1)!.options
    expect(submitted.method).toBe('POST')
    expect(submitted.credentials).toBe('include')
    expect(new Headers(submitted.headers).get('X-CSRF-TOKEN')).toBe('anonymous-install-csrf')
    expect(JSON.parse(String(submitted.body))).toEqual({
      token: 'test-install-token', userName: '测试管理员', email: 'admin@example.test', password: 'test-password-123456',
      storagePath: '/srv/recordings', mongoConnectionString: 'mongodb://127.0.0.1:27017', databaseName: 'recorder-install-test',
      ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe',
    })
  })
})
