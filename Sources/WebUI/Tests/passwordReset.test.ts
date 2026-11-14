// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { AuthAPI } from '../src/apis/AuthAPI'
import { useAuthStore } from '../src/stores/auth'
import { i18n } from '../src/plugins/i18n'
import ForgotPasswordView from '../src/views/ForgotPasswordView.vue'
import ResetPasswordView from '../src/views/ResetPasswordView.vue'

let pinia: Pinia
let router: Router
const mounted: VueWrapper[] = []
const token = 'A'.repeat(43)
const container = { template: '<div><slot /></div>' }
const stubs = {
  VCard: container, AppBrand: true, VProgressLinear: true,
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VBtn: { props: ['disabled', 'loading', 'type'], template: '<button :type="type" :disabled="disabled || loading"><slot /></button>' },
  VTextField: { props: ['modelValue', 'label', 'disabled'], emits: ['update:modelValue'], template: '<label>{{ label }}<input :value="modelValue" :aria-label="label" :disabled="disabled" @input="$emit(\'update:modelValue\', $event.target.value)" /></label>' },
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  router = createRouter({ history: createMemoryHistory(), routes: ['/forgot-password', '/reset-password', '/login'].map(path => ({ path, component: container })) })
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'passwordResetOptions').mockResolvedValue({ enabled: true })
  vi.spyOn(AuthAPI, 'requestPasswordReset').mockResolvedValue()
  vi.spyOn(AuthAPI, 'completePasswordReset').mockResolvedValue()
  vi.spyOn(useAuthStore(), 'refresh').mockResolvedValue()
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  window.history.replaceState({}, '', '/')
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

async function openReset(value = token): Promise<VueWrapper>
{
  const path = `/reset-password#token=${value}`
  await router.push(path)
  window.history.replaceState({}, '', path)
  const wrapper = mount(ResetPasswordView, { global: { plugins: [pinia, router, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

async function fillPassword(wrapper: VueWrapper, password = 'new-password-1234', confirmation = password): Promise<void>
{
  await wrapper.get('input[aria-label="新密码"]').setValue(password)
  await wrapper.get('input[aria-label="确认新密码"]').setValue(confirmation)
}

describe('邮件密码恢复', () =>
{
  it('申请使用统一提示，不根据账户是否存在展示不同状态', async () =>
  {
    await router.push('/forgot-password')
    const wrapper = mount(ForgotPasswordView, { global: { plugins: [pinia, router, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await wrapper.get('input').setValue('viewer@example.test')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(AuthAPI.requestPasswordReset).toHaveBeenCalledExactlyOnceWith('viewer@example.test')
    expect(wrapper.text()).toContain('如果该邮箱已注册')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('未配置恢复服务时明确提示且不提交邮箱', async () =>
  {
    vi.mocked(AuthAPI.passwordResetOptions).mockResolvedValue({ enabled: false })
    await router.push('/forgot-password')
    const wrapper = mount(ForgotPasswordView, { global: { plugins: [pinia, router, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('本站暂未启用邮件密码恢复')
    expect(wrapper.find('form').exists()).toBe(false)
    expect(AuthAPI.requestPasswordReset).not.toHaveBeenCalled()
  })

  it('打开链接仅读取并清除片段，提交才消费令牌并退出旧登录', async () =>
  {
    const auth = useAuthStore()
    auth.user = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: ['recordings.view'] }
    const wrapper = await openReset()
    expect(window.location.hash).toBe('')
    expect(router.currentRoute.value.hash).toBe('')
    expect(AuthAPI.completePasswordReset).not.toHaveBeenCalled()
    await fillPassword(wrapper)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(AuthAPI.completePasswordReset).toHaveBeenCalledExactlyOnceWith(token, 'new-password-1234')
    expect(auth.user).toBeNull()
    expect(auth.refresh).toHaveBeenCalledOnce()
    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.passwordReset).toBe('success')
  })

  it.each([['short', 'short', '12 至 256'], ['valid-password-1234', 'different-password', '两次输入的密码不一致']])('拒绝无效密码组合 %s', async (password, confirmation, message) =>
  {
    const wrapper = await openReset()
    await fillPassword(wrapper, password, confirmation)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain(message)
    expect(AuthAPI.completePasswordReset).not.toHaveBeenCalled()
  })

  it('缺少或损坏的令牌不能提交', async () =>
  {
    const wrapper = await openReset('invalid')
    expect(wrapper.text()).toContain('重设链接无效')
    expect(wrapper.find('form').exists()).toBe(false)
    expect(AuthAPI.completePasswordReset).not.toHaveBeenCalled()
  })

  it('改密成功后刷新会话失败，重试返回登录不会再次消费令牌', async () =>
  {
    vi.mocked(useAuthStore().refresh).mockRejectedValueOnce(new Error('连接失败')).mockResolvedValue()
    const wrapper = await openReset()
    await fillPassword(wrapper)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('密码已重设')
    expect(wrapper.text()).toContain('连接失败')
    expect(wrapper.find('form').exists()).toBe(false)
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(AuthAPI.completePasswordReset).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.path).toBe('/login')
  })
})
