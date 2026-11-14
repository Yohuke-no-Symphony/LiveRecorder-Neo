// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { i18n } from '../src/plugins/i18n'
import type { BilibiliAccount, BilibiliLoginSession } from '../src/models/auth'
import AccountsView from '../src/views/AccountsView.vue'
import BilibiliAccountCard from '../src/components/accounts/BilibiliAccountCard.vue'

vi.mock('qrcode', () => ({ default: { toDataURL: vi.fn(async (value: string) => `data:image/png;base64,${value}`) } }))
const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }
const tabs = { name: 'LoginTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div><slot /></div>' }
const accounts: BilibiliAccount[] = [
  { id: 'first', uid: 10, userName: '账号一', avatarUrl: '', status: 'Expired', isDefault: true },
  { id: 'second', uid: 20, userName: '账号二', avatarUrl: '', status: 'Valid', isEffectiveDefault: true },
]
const selectedAccounts = accounts.map(account => ({ ...account, isDefault: account.id === 'second', isEffectiveDefault: account.id === 'second' }))
const waiting: BilibiliLoginSession = { sessionId: 'qr-session', status: 'Waiting' }
const stubs = {
  PageHeader: container, EmptyState: true, BilibiliAccountCard: true, ConfirmDialog: true,
  VProgressCircular: true, VIcon: true, VTabs: tabs, VTab: container, CaptchaChallenge: true,
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VDialog: { props: ['modelValue'], emits: ['update:modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  ContentCard: { props: ['title'], template: '<section><h2>{{ title }}</h2><slot /><slot name="actions" /></section>' },
  VBtn: { props: ['disabled', 'loading', 'type'], template: '<button :type="type" :disabled="disabled || loading"><slot /></button>' },
  VTextField: { props: ['modelValue', 'label', 'prefix'], emits: ['update:modelValue'], template: '<label>{{ label }}<input :value="modelValue" :aria-label="label" @input="$emit(\'update:modelValue\', $event.target.value)" /></label>' },
}

beforeEach(() =>
{
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'bilibiliAccounts').mockResolvedValue(accounts)
  vi.spyOn(AuthAPI, 'createQrLogin').mockResolvedValue({ ...waiting, qrUrl: 'new-qr' })
  vi.spyOn(AuthAPI, 'pollQrLogin').mockResolvedValue({ ...waiting, status: 'Expired' })
  vi.spyOn(AuthAPI, 'createSmsLogin').mockResolvedValue({ sessionId: 'sms-session', status: 'SmsSent' })
  vi.spyOn(AuthAPI, 'completeSms').mockResolvedValue({ sessionId: 'sms-session', status: 'Complete' })
  vi.spyOn(AuthAPI, 'setDefaultBilibiliAccount').mockResolvedValue(selectedAccounts)
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  vi.useRealTimers()
})

async function openAccounts(): Promise<VueWrapper>
{
  const wrapper = mount(AccountsView, { global: { plugins: [i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

async function relogin(wrapper: VueWrapper, index = 0): Promise<void>
{
  wrapper.findAllComponents(BilibiliAccountCard)[index]!.vm.$emit('relogin', accounts[index])
  await flushPromises()
}

function button(wrapper: VueWrapper, title: string)
{
  return wrapper.findAll('button').find(item => item.text() === title)!
}

describe('默认采集账号与定向重新登录', () =>
{
  it('设置默认使用接口返回的列表，不额外 GET，回退提示随结果消失', async () =>
  {
    const wrapper = await openAccounts()
    expect(wrapper.text()).toContain('默认账号暂不可用，当前使用 账号二')
    wrapper.findAllComponents(BilibiliAccountCard)[1]!.vm.$emit('setDefault', 'second')
    await flushPromises()
    expect(AuthAPI.setDefaultBilibiliAccount).toHaveBeenCalledExactlyOnceWith('second')
    expect(AuthAPI.bilibiliAccounts).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).not.toContain('默认账号暂不可用')
    expect(wrapper.findAllComponents(BilibiliAccountCard)[1]!.props('account')).toMatchObject({ id: 'second', isDefault: true })
  })

  it('设置默认成功后，在途的旧列表轮询不能覆盖返回结果', async () =>
  {
    let completeSelection!: (value: BilibiliAccount[]) => void
    let completePolling!: (value: BilibiliAccount[]) => void
    vi.mocked(AuthAPI.setDefaultBilibiliAccount).mockImplementation(() => new Promise(resolve => { completeSelection = resolve }))
    const wrapper = await openAccounts()
    vi.mocked(AuthAPI.bilibiliAccounts).mockImplementationOnce(() => new Promise(resolve => { completePolling = resolve }))
    wrapper.findAllComponents(BilibiliAccountCard)[1]!.vm.$emit('setDefault', 'second')
    await vi.advanceTimersByTimeAsync(30000)
    expect(AuthAPI.bilibiliAccounts).toHaveBeenCalledTimes(2)
    completeSelection(selectedAccounts)
    await flushPromises()
    completePolling(accounts)
    await flushPromises()
    expect(AuthAPI.bilibiliAccounts).toHaveBeenCalledTimes(2)
    expect(wrapper.findAllComponents(BilibiliAccountCard)[0]!.props('account')).toMatchObject({ id: 'first', isDefault: false })
    expect(wrapper.findAllComponents(BilibiliAccountCard)[1]!.props('account')).toMatchObject({ id: 'second', isDefault: true })
    expect(wrapper.text()).not.toContain('默认账号暂不可用')
  })

  it('刷新二维码与切换短信都保留目标账号，关闭后新增模式不继承目标', async () =>
  {
    const wrapper = await openAccounts()
    await relogin(wrapper)
    expect(wrapper.text()).toContain('请使用 账号一（UID 10）登录')
    expect(AuthAPI.createQrLogin).toHaveBeenLastCalledWith('first')
    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    await button(wrapper, '刷新二维码').trigger('click')
    await flushPromises()
    expect(AuthAPI.createQrLogin).toHaveBeenLastCalledWith('first')
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'sms')
    await nextTick()
    await wrapper.get('input[aria-label="手机号"]').setValue('12345678901')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(AuthAPI.createSmsLogin).toHaveBeenCalledExactlyOnceWith('86', '12345678901', 'first')
    await button(wrapper, '关闭').trigger('click')
    await button(wrapper, '添加账号').trigger('click')
    await flushPromises()
    expect(AuthAPI.createQrLogin).toHaveBeenLastCalledWith(undefined)
    expect(wrapper.text()).not.toContain('请使用 账号一')
  })

  it('前一次扫码请求晚到或失败都不会污染新目标登录', async () =>
  {
    let rejectPrevious!: (error: Error) => void
    vi.mocked(AuthAPI.createQrLogin).mockImplementationOnce(() => new Promise((_, reject) => { rejectPrevious = reject }))
    const wrapper = await openAccounts()
    await relogin(wrapper)
    await button(wrapper, '关闭').trigger('click')
    await relogin(wrapper, 1)
    rejectPrevious(new Error('旧登录错误'))
    await flushPromises()
    expect(wrapper.text()).toContain('请使用 账号二（UID 20）登录')
    expect(wrapper.text()).not.toContain('旧登录错误')
    expect(wrapper.get('img').attributes('src')).toContain('new-qr')
  })

  it('短信完成晚于切换二维码时不改写当前会话或刷新账号', async () =>
  {
    let completePrevious!: (session: BilibiliLoginSession) => void
    vi.mocked(AuthAPI.completeSms).mockImplementation(() => new Promise(resolve => { completePrevious = resolve }))
    const wrapper = await openAccounts()
    await relogin(wrapper)
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'sms')
    await nextTick()
    await wrapper.get('input[aria-label="手机号"]').setValue('12345678901')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    await wrapper.get('input[aria-label="短信验证码"]').setValue('123456')
    await wrapper.get('form').trigger('submit')
    wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'qr')
    await flushPromises()
    completePrevious({ sessionId: 'sms-session', status: 'Complete' })
    await flushPromises()
    expect(wrapper.text()).not.toContain('登录成功')
    expect(wrapper.find('img').exists()).toBe(true)
    expect(AuthAPI.bilibiliAccounts).toHaveBeenCalledTimes(1)
  })
})
