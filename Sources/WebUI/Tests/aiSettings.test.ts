// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, reactive } from 'vue'
import AiSettingsPanel from '../src/components/common/AiSettingsPanel.vue'
import { AiSettingsAPI } from '../src/apis/AiSettingsAPI'
import { defaultAiSettings, isValidAiContextWindow, isValidAiRequestLimit, normalizeAiSettings, type AiSettings } from '../src/models/settings'
import { i18n } from '../src/plugins/i18n'

const container = { template: '<div><slot /></div>' }
const field = defineComponent({
  props: ['modelValue', 'label', 'type', 'disabled', 'readonly', 'min', 'max', 'step', 'errorMessages'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('input', {
      value: props.modelValue, 'aria-label': props.label, type: props.type ?? 'text',
      disabled: props.disabled, readonly: props.readonly, min: props.min, max: props.max, step: props.step,
      'data-error': props.errorMessages || undefined,
      onInput: (event: Event) => emit('update:modelValue', props.type === 'number'
        ? Number((event.target as HTMLInputElement).value) : (event.target as HTMLInputElement).value),
    })
  },
})
const select = defineComponent({
  props: ['modelValue', 'label', 'items', 'disabled'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('select', {
      value: props.modelValue, 'aria-label': props.label, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value),
    }, (props.items as (string | { title: string; value: string })[]).map(item =>
      h('option', { value: typeof item === 'string' ? item : item.value }, typeof item === 'string' ? item : item.title)))
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
  ContentCard: container, VTextField: field, VSelect: select, VSwitch: toggle, VCheckbox: toggle,
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VAlert: { props: ['type'], template: '<div role="alert" :data-type="type"><slot /></div>' },
}
const mounted: VueWrapper[] = []

function openSettings(overrides: Partial<AiSettings> = {})
{
  const settings = reactive({ ...defaultAiSettings(), model: 'test-model', ...overrides })
  const wrapper = mount(AiSettingsPanel, { props: { modelValue: settings }, global: { plugins: [i18n], stubs } })
  mounted.push(wrapper)
  return { settings, wrapper }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}

beforeEach(() => { i18n.global.locale.value = 'zh-CN' })
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  i18n.global.locale.value = 'zh-CN'
})

describe('AI 可配置执行额度', () =>
{
  it('支持百万 token 参考值与输出配置，调用次数可设为零不限', async () =>
  {
    const { wrapper, settings } = openSettings()
    const output = wrapper.get<HTMLInputElement>('input[aria-label="单次输出 token 数"]')
    const requests = wrapper.get<HTMLInputElement>('input[aria-label="每场模型请求次数（0 不限）"]')
    const calls = wrapper.get<HTMLInputElement>('input[aria-label="每场工具调用次数（0 不限）"]')
    expect(output.element.value).toBe('2048')
    expect(requests.element.value).toBe('0')
    expect(calls.element.value).toBe('0')
    expect(output.attributes()).toMatchObject({ min: '1', step: '1' })
    expect(requests.attributes()).toMatchObject({ min: '0', step: '1' })
    expect(calls.attributes()).toMatchObject({ min: '0', step: '1' })
    const maximum = wrapper.get<HTMLInputElement>('input[aria-label="每场最高参考 token 数"]')
    expect(maximum.element.value).toBe('1000000')
    for (const input of wrapper.findAll('input[type="number"]'))
    {
      expect(input.attributes('max')).toBeUndefined()
    }
    await wrapper.get('input[aria-label="每小时录像参考 token 数"]').setValue('1000000')
    await wrapper.get('input[aria-label="每场最低参考 token 数"]').setValue('1000000')
    await maximum.setValue('2000000')
    await output.setValue('1000000')
    await requests.setValue('0')
    await calls.setValue('0')
    expect(settings).toMatchObject({ tokensPerHour: 1000000, minimumTokens: 1000000, maximumTokens: 2000000, maxOutputTokens: 1000000, maxModelRequests: 0, maxToolCalls: 0 })
    expect(output.element.checkValidity()).toBe(true)
    expect(maximum.element.checkValidity()).toBe(true)
    expect(wrapper.text()).not.toContain('2,048')
    expect(wrapper.text()).not.toContain('aiSettings.limits')
    await wrapper.setProps({ disabled: true })
    expect(output.element.disabled).toBe(true)
    expect(requests.element.disabled).toBe(true)
    expect(calls.element.disabled).toBe(true)
  })

  it('模型上下文窗口默认自动且支持百万以上容量，不套用旧压缩阈值', async () =>
  {
    const { wrapper, settings } = openSettings({ maxOutputTokens: 1000000, contextTokenTarget: 32000 })
    const window = wrapper.get<HTMLInputElement>('input[aria-label="模型上下文窗口（0 自动）"]')
    expect(window.element.value).toBe('0')
    expect(window.attributes()).toMatchObject({ min: '0', step: '1' })
    expect(window.attributes('max')).toBeUndefined()
    expect(window.attributes('data-error')).toBeUndefined()
    expect(wrapper.find('input[aria-label="上下文压缩阈值（tokens）"]').exists()).toBe(false)
    await window.setValue('2000000')
    expect(window.element.checkValidity()).toBe(true)
    expect(window.attributes('data-error')).toBeUndefined()
    expect(settings).toMatchObject({ contextWindowTokens: 2000000, contextTokenTarget: 32000, maximumTokens: 1000000, maxOutputTokens: 1000000 })
    await window.setValue('1000000')
    expect(window.attributes('data-error')).toBeTruthy()
    expect(wrapper.get('button').attributes('disabled')).toBeDefined()
    await window.setValue('8191')
    expect(window.attributes('data-error')).toBeTruthy()
    await window.setValue('0')
    expect(window.attributes('data-error')).toBeUndefined()
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
    await wrapper.setProps({ disabled: true })
    expect(window.element.disabled).toBe(true)
  })

  it('读取旧设置时窗口为未知，明确保存的窗口和旧调用额度保持原值', () =>
  {
    const legacy = normalizeAiSettings({ contextTokenTarget: 32000, maxModelRequests: 24, maxToolCalls: 48 })
    expect(legacy).toMatchObject({ contextWindowTokens: 0, contextTokenTarget: 32000, maxModelRequests: 24, maxToolCalls: 48, requestTimeoutSeconds: 180, maxRequestRetries: 2 })
    expect(normalizeAiSettings({ contextWindowTokens: 2000000 })).toMatchObject({ contextWindowTokens: 2000000 })
    expect(normalizeAiSettings().contextWindowTokens).toBe(0)
    expect(normalizeAiSettings({ requestTimeoutSeconds: 0, maxRequestRetries: 0 })).toMatchObject({ requestTimeoutSeconds: 0, maxRequestRetries: 0 })
    expect(normalizeAiSettings({ requestTimeoutSeconds: 600, maxRequestRetries: 5 })).toMatchObject({ requestTimeoutSeconds: 600, maxRequestRetries: 5 })
  })

  it('无响应超时与单次重试上限可配置为零，并阻止无效请求参数测试', async () =>
  {
    const test = vi.spyOn(AiSettingsAPI, 'test').mockResolvedValue({ success: true, promptTokens: 1, completionTokens: 1 })
    const { wrapper, settings } = openSettings()
    const timeout = wrapper.get<HTMLInputElement>('input[aria-label="无响应超时（秒，0 不限）"]')
    const retries = wrapper.get<HTMLInputElement>('input[aria-label="单次请求重试上限（0 关闭）"]')
    expect(timeout.element.value).toBe('180')
    expect(retries.element.value).toBe('2')
    expect(timeout.attributes()).toMatchObject({ min: '0', step: '1' })
    expect(retries.attributes()).toMatchObject({ min: '0', step: '1' })
    for (const field of [timeout, retries])
    {
      await field.setValue('2147483647')
      expect(field.attributes('data-error')).toBeUndefined()
      for (const invalid of ['2147483648', '-1', '1.5'])
      {
        await field.setValue(invalid)
        expect(field.attributes('data-error')).toBeTruthy()
        expect(wrapper.get('button').attributes('disabled')).toBeDefined()
        await wrapper.get('button').trigger('click')
        expect(test).not.toHaveBeenCalled()
      }
      await field.setValue('0')
      expect(field.attributes('data-error')).toBeUndefined()
    }
    expect(settings).toMatchObject({ requestTimeoutSeconds: 0, maxRequestRetries: 0 })
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
    await wrapper.setProps({ disabled: true })
    expect(timeout.element.disabled).toBe(true)
    expect(retries.element.disabled).toBe(true)
  })

  it.each([
    [0, true], [180, true], [2147483647, true], [-1, false], [1.5, false],
    [2147483648, false], [Number.NaN, false], [Number.POSITIVE_INFINITY, false],
  ])('请求参数 %s 的本地整数校验为 %s', (value, valid) =>
  {
    expect(isValidAiRequestLimit(value)).toBe(valid)
  })

  it.each([
    [0, 1000000, true], [8192, 2048, true], [2000000, 1000000, true],
    [-1, 2048, false], [1, 2048, false], [8191, 2048, false], [8192, 8192, false],
    [8192.5, 2048, false], [Number.NaN, 2048, false],
  ])('窗口 %s / 输出 %s 的本地校验为 %s', (contextWindowTokens, maxOutputTokens, valid) =>
  {
    expect(isValidAiContextWindow({ contextWindowTokens, maxOutputTokens })).toBe(valid)
  })

  it('保留已保存的参考值而不套用新的默认值', () =>
  {
    const { wrapper, settings } = openSettings({ maximumTokens: 100000, maxModelRequests: 12, maxToolCalls: 32 })
    expect(wrapper.get<HTMLInputElement>('input[aria-label="每场最高参考 token 数"]').element.value).toBe('100000')
    expect(settings).toMatchObject({ maximumTokens: 100000, maxModelRequests: 12, maxToolCalls: 32 })
  })

  it('DeepSeek 显示思考设置，禁用时保留但不可编辑强度，其他服务不显示', async () =>
  {
    const { wrapper, settings } = openSettings({ provider: 'DeepSeek' })
    const mode = wrapper.get<HTMLSelectElement>('select[aria-label="思考模式"]')
    const effort = wrapper.get<HTMLSelectElement>('select[aria-label="思考强度"]')
    expect(mode.element.value).toBe('Default')
    expect(effort.element.value).toBe('Default')
    expect(mode.text()).toContain('跟随模型')
    await mode.setValue('Enabled')
    await effort.setValue('Max')
    expect(settings).toMatchObject({ thinkingMode: 'Enabled', reasoningEffort: 'Max' })
    await mode.setValue('Disabled')
    expect(effort.element.disabled).toBe(true)
    expect(settings.reasoningEffort).toBe('Max')
    await mode.setValue('Enabled')
    expect(effort.element.disabled).toBe(false)
    await wrapper.get('select[aria-label="服务商"]').setValue('OpenAI')
    expect(wrapper.find('select[aria-label="思考模式"]').exists()).toBe(false)
    expect(wrapper.find('select[aria-label="思考强度"]').exists()).toBe(false)
    expect(settings.baseUrl).toBe('https://api.openai.com/v1')
  })

  it.each(['zh-CN', 'en-US', 'ja-JP'] as const)('%s 提供新字段文案且不保留旧 limits 键', language =>
  {
    for (const key of ['maxOutputTokens', 'contextWindowTokens', 'invalidContextWindow', 'requestTimeoutSeconds', 'maxRequestRetries', 'invalidRequestLimit', 'maxModelRequests', 'maxToolCalls', 'thinkingMode', 'reasoningEffort', 'followModel', 'thinkingEnabled', 'thinkingDisabled', 'effortLow', 'effortHigh', 'effortMax'])
    {
      expect(i18n.global.te(`aiSettings.${key}`, language)).toBe(true)
    }
    expect(i18n.global.te('aiSettings.limits', language)).toBe(false)
    expect(i18n.global.te('aiSettings.contextTokenTarget', language)).toBe(false)
  })
})

describe('AI 连接测试结果与配置绑定', () =>
{
  it.each([
    { model: 'new-model' }, { baseUrl: 'https://different.example.test' }, { provider: 'DeepSeek' as const },
    { apiKey: 'new-test-key' }, { clearApiKey: true }, { thinkingMode: 'Disabled' as const }, { reasoningEffort: 'High' as const },
    { contextWindowTokens: 1000000 }, { requestTimeoutSeconds: 300 }, { maxRequestRetries: 0 },
  ])('配置改变后清除过期成功提示 %j', async change =>
  {
    vi.spyOn(AiSettingsAPI, 'test').mockResolvedValue({ success: true, promptTokens: 10, completionTokens: 2 })
    const { wrapper, settings } = openSettings()
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-type="success"]').exists()).toBe(true)
    Object.assign(settings, change)
    await nextTick()
    expect(wrapper.find('[data-type="success"]').exists()).toBe(false)
  })

  it('发送独立配置快照，配置修改后旧成功结果不再显示，即使用户改回原值', async () =>
  {
    const pending = deferred<{ success: boolean; promptTokens: number; completionTokens: number }>()
    const test = vi.spyOn(AiSettingsAPI, 'test').mockReturnValue(pending.promise)
    const { wrapper, settings } = openSettings({ maxOutputTokens: 8192, maxModelRequests: 16, maxToolCalls: 96, thinkingMode: 'Enabled', reasoningEffort: 'High', requestTimeoutSeconds: 600, maxRequestRetries: 5 })
    await wrapper.get('button').trigger('click')
    const snapshot = test.mock.calls[0]![0]
    expect(snapshot).not.toBe(settings)
    expect(snapshot).toMatchObject({ maxOutputTokens: 8192, maxModelRequests: 16, maxToolCalls: 96, thinkingMode: 'Enabled', reasoningEffort: 'High', requestTimeoutSeconds: 600, maxRequestRetries: 5 })
    settings.model = 'changed'
    expect(snapshot.model).toBe('test-model')
    settings.model = 'test-model'
    pending.resolve({ success: true, promptTokens: 10, completionTokens: 2 })
    await flushPromises()
    expect(wrapper.find('[data-type="success"]').exists()).toBe(false)
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
  })

  it('配置修改后的旧失败响应不会污染新配置的状态', async () =>
  {
    const pending = deferred<{ success: boolean; promptTokens: number; completionTokens: number }>()
    vi.spyOn(AiSettingsAPI, 'test').mockReturnValue(pending.promise)
    const { wrapper, settings } = openSettings()
    await wrapper.get('button').trigger('click')
    settings.apiKey = 'updated-test-key'
    pending.reject(new Error('旧密钥失败'))
    await flushPromises()
    expect(wrapper.find('[data-type="error"]').exists()).toBe(false)
    expect(wrapper.find('[data-type="success"]').exists()).toBe(false)
  })
})
