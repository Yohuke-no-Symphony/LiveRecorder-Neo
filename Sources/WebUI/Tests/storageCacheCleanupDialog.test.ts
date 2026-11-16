// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import StorageCacheCleanupDialog from '../src/components/storage/StorageCacheCleanupDialog.vue'
import CardHeader from '../src/components/common/CardHeader.vue'
import type { CacheCleanupResult } from '../src/models/storage'
import { i18n } from '../src/plugins/i18n'
import { formatBytes, formatNumber } from '../src/utilities/format'

const { cleanupCache } = vi.hoisted(() => ({ cleanupCache: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { cleanupCache } }))

const container = { template: '<div><slot /></div>' }
const dialog = defineComponent({
  name: 'VDialog', props: { modelValue: Boolean, persistent: Boolean, scrollable: Boolean }, emits: ['update:modelValue'],
  setup(props, { slots })
  {
    return () => props.modelValue ? h('section', { role: 'dialog' }, slots.default?.()) : null
  },
})
const button = defineComponent({
  name: 'VBtn',
  props: ['disabled', 'loading', 'type', 'form', 'color'],
  setup(props, { slots })
  {
    return () => h('button', {
      type: props.type ?? 'button', form: props.form, disabled: props.disabled || props.loading,
    }, slots.default?.())
  },
})
const checkbox = defineComponent({
  props: ['modelValue', 'value', 'label', 'disabled'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [h('input', {
      type: 'checkbox', value: props.value, disabled: props.disabled,
      checked: Array.isArray(props.modelValue) ? props.modelValue.includes(props.value) : props.modelValue,
      onChange: (event: Event) =>
      {
        const selected = (event.target as HTMLInputElement).checked
        emit('update:modelValue', Array.isArray(props.modelValue)
          ? selected ? [...props.modelValue, props.value] : props.modelValue.filter((value: string) => value !== props.value)
          : selected)
      },
    }), props.label])
  },
})
const select = defineComponent({
  props: ['modelValue', 'items', 'label', 'disabled'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('select', {
      value: props.modelValue, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', Number((event.target as HTMLSelectElement).value)),
    }, props.items.map((item: { title: string; value: number }) => h('option', { value: item.value }, item.title)))])
  },
})
const mounted: VueWrapper[] = []
const categories = ['playback', 'artwork', 'analyticsContributions'] as const
const stubs = {
  VDialog: dialog, VCard: container, VCardText: container, VBtn: button, VCheckbox: checkbox, VSelect: select,
  VAlert: { template: '<div role="alert"><slot /></div>' },
}

function result(): CacheCleanupResult
{
  return {
    categories: [
      { category: 'playback', deletedFiles: 2, deletedBytes: 2048, skippedEntries: 1, failedEntries: 0 },
      { category: 'artwork', deletedFiles: 3, deletedBytes: 512, skippedEntries: 0, failedEntries: 1 },
      { category: 'analyticsContributions', deletedFiles: 1, deletedBytes: 1024, skippedEntries: 2, failedEntries: 0 },
    ],
    deletedFiles: 6, deletedBytes: 3584, skippedEntries: 3, failedEntries: 1,
  }
}

function open(props: Record<string, unknown> = {}): VueWrapper
{
  const wrapper = mount(StorageCacheCleanupDialog, {
    props: { modelValue: true, ...props }, attachTo: document.body,
    global: { plugins: [i18n], stubs },
  })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() =>
{
  cleanupCache.mockReset()
  cleanupCache.mockResolvedValue(result())
  i18n.global.locale.value = 'zh-CN'
})

afterEach(() =>
{
  for (const wrapper of mounted.splice(0)) { wrapper.unmount() }
  i18n.global.locale.value = 'zh-CN'
})

describe('管理员缓存清理选项', () =>
{
  it('默认全选三类缓存和全部不活跃时间，外置提交按钮关联原生表单', () =>
  {
    const first = open()
    expect(first.findAll<HTMLInputElement>('input[type="checkbox"]').map(input => [input.element.value, input.element.checked]))
      .toEqual(categories.map(category => [category, true]))
    expect(first.get<HTMLSelectElement>('select').element.value).toBe('0')
    expect(first.findAll<HTMLOptionElement>('option').map(option => Number(option.element.value))).toEqual([0, 60, 1440, 10080, 43200])
    expect(first.findComponent(CardHeader).props('title')).toBe(i18n.global.t('views.storage.cleanup.title'))
    const titleId = first.get('[role="dialog"]').attributes('aria-labelledby')
    expect(first.get('h2').attributes('id')).toBe(titleId)
    const form = first.get<HTMLFormElement>('form')
    const submit = first.get<HTMLButtonElement>('button[type="submit"]')
    expect(form.attributes('id')).toBeTruthy()
    expect(submit.element.form).toBe(form.element)
    expect(form.element.contains(submit.element)).toBe(false)
    const submitComponent = first.findAllComponents({ name: 'VBtn' }).find(item => item.props('type') === 'submit')
    expect(submitComponent).toBeDefined()
    expect(submitComponent!.props('color')).toBe('error')
    expect(first.findComponent(dialog).props('scrollable')).toBe(true)
    expect(cleanupCache).not.toHaveBeenCalled()
  })

  it('同一页面的多个实例各自关联不同的表单 ID', () =>
  {
    const wrapper = mount({
      components: { StorageCacheCleanupDialog },
      template: '<div><StorageCacheCleanupDialog :model-value="true" /><StorageCacheCleanupDialog :model-value="true" /></div>',
    }, { attachTo: document.body, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    const forms = wrapper.findAll<HTMLFormElement>('form')
    const buttons = wrapper.findAll<HTMLButtonElement>('button[type="submit"]')
    expect(forms).toHaveLength(2)
    expect(new Set(forms.map(form => form.attributes('id'))).size).toBe(2)
    expect(buttons.map(item => item.element.form)).toEqual(forms.map(form => form.element))
  })

  it('原生表单提交只发送所选类别和未使用时间', async () =>
  {
    const wrapper = open()
    await wrapper.get<HTMLInputElement>('input[value="artwork"]').setValue(false)
    await wrapper.get('select').setValue('10080')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(cleanupCache).toHaveBeenCalledExactlyOnceWith({ categories: ['playback', 'analyticsContributions'], minimumUnusedMinutes: 10080 })
  })

  it('未选择类别时禁用清理，直接提交表单也不会发起请求', async () =>
  {
    const wrapper = open()
    for (const input of wrapper.findAll<HTMLInputElement>('input[type="checkbox"]')) { await input.setValue(false) }
    expect(wrapper.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(true)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(cleanupCache).not.toHaveBeenCalled()
  })

  it('清理期间禁用编辑和取消，拦截遮罩关闭与重复提交，结束忙碌后才通知父页刷新', async () =>
  {
    let finish!: (value: CacheCleanupResult) => void
    cleanupCache.mockReturnValueOnce(new Promise<CacheCleanupResult>(resolve => { finish = resolve }))
    const events: (boolean | string)[] = []
    const wrapper = open({ onBusy: (busy: boolean) => events.push(busy), onCleaned: () => events.push('cleaned') })
    const form = wrapper.get('form')
    await form.trigger('submit')

    expect(wrapper.findComponent(dialog).props('persistent')).toBe(true)
    expect(wrapper.findAll<HTMLInputElement>('input[type="checkbox"]').every(input => input.element.disabled)).toBe(true)
    expect(wrapper.get<HTMLSelectElement>('select').element.disabled).toBe(true)
    expect(wrapper.findAll<HTMLButtonElement>('button').every(item => item.element.disabled)).toBe(true)
    wrapper.findComponent(dialog).vm.$emit('update:modelValue', false)
    await form.trigger('submit')
    await nextTick()
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(cleanupCache).toHaveBeenCalledTimes(1)
    expect(events).toEqual([true])

    finish(result())
    await flushPromises()
    expect(events).toEqual([true, false, 'cleaned'])
    expect(wrapper.findComponent(dialog).props('persistent')).toBe(false)
    expect(wrapper.emitted('cleaned')).toEqual([[result()]])
    await form.trigger('submit')
    await flushPromises()
    expect(cleanupCache).toHaveBeenCalledTimes(1)
    expect(wrapper.emitted('cleaned')).toHaveLength(1)
  })

  it('请求失败保留类别和时间草稿，可重新提交而不会提前发出成功事件', async () =>
  {
    cleanupCache.mockRejectedValueOnce(new Error('缓存目录无法读取'))
    const wrapper = open()
    await wrapper.get('input[value="artwork"]').setValue(false)
    await wrapper.get('select').setValue('1440')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('缓存目录无法读取')
    expect(wrapper.get<HTMLInputElement>('input[value="artwork"]').element.checked).toBe(false)
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('1440')
    expect(wrapper.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(false)
    expect(wrapper.emitted('cleaned')).toBeUndefined()

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(cleanupCache).toHaveBeenCalledTimes(2)
    expect(cleanupCache).toHaveBeenLastCalledWith({ categories: ['playback', 'analyticsContributions'], minimumUnusedMinutes: 1440 })
    expect(wrapper.emitted('cleaned')).toEqual([[result()]])
  })

  it('成功后保留总量、分类及跳过失败统计，只提供完成按钮并允许关闭', async () =>
  {
    const wrapper = open()
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const cleanup = result()
    const status = wrapper.get('[role="status"]')
    expect(status.text()).toContain(i18n.global.t('views.storage.cleanup.completed', { files: formatNumber(cleanup.deletedFiles), bytes: formatBytes(cleanup.deletedBytes) }))
    for (const category of cleanup.categories)
    {
      expect(status.text()).toContain(i18n.global.t(`views.storage.cleanup.categoryLabels.${category.category}`))
      expect(status.text()).toContain(i18n.global.t('views.storage.cleanup.categoryResult', { files: formatNumber(category.deletedFiles), bytes: formatBytes(category.deletedBytes) }))
    }
    expect(status.text()).toContain(i18n.global.t('views.storage.cleanup.skipped', { count: formatNumber(cleanup.skippedEntries) }))
    expect(status.text()).toContain(i18n.global.t('views.storage.cleanup.failed', { count: formatNumber(cleanup.failedEntries) }))
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.findAll('button')).toHaveLength(1)
    expect(wrapper.get('button').text()).toBe(i18n.global.t('views.common.done'))
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
    expect(wrapper.emitted('cleaned')).toHaveLength(1)
  })

  it('关闭失败的对话框后重新打开，清空错误并恢复默认选项', async () =>
  {
    cleanupCache.mockRejectedValueOnce(new Error('缓存目录无法读取'))
    const wrapper = open()
    await wrapper.get('input[value="playback"]').setValue(false)
    await wrapper.get('select').setValue('43200')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true })

    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.findAll<HTMLInputElement>('input[type="checkbox"]').every(input => input.element.checked)).toBe(true)
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('0')
    expect(wrapper.emitted('cleaned')).toBeUndefined()
  })

  it('重新打开成功的对话框后清空结果，恢复选项并可以执行新一轮清理', async () =>
  {
    const wrapper = open()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true })

    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.findAll<HTMLInputElement>('input[type="checkbox"]').every(input => input.element.checked)).toBe(true)
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('0')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(cleanupCache).toHaveBeenCalledTimes(2)
    expect(wrapper.emitted('cleaned')).toHaveLength(2)
  })

  it('空闲时允许取消或通过对话框关闭事件退出', async () =>
  {
    const cancel = open()
    await cancel.findAll('button').find(item => item.text() === i18n.global.t('views.common.cancel'))!.trigger('click')
    expect(cancel.emitted('update:modelValue')).toEqual([[false]])

    const overlay = open()
    overlay.findComponent(dialog).vm.$emit('update:modelValue', false)
    await nextTick()
    expect(overlay.emitted('update:modelValue')).toEqual([[false]])
    expect(cleanupCache).not.toHaveBeenCalled()
  })

  it('已打开选项和成功结果随三语切换立即更新', async () =>
  {
    const wrapper = open()
    for (const language of ['zh-CN', 'en-US', 'ja-JP'] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      const title = i18n.global.t('views.storage.cleanup.title')
      expect(title).not.toBe('views.storage.cleanup.title')
      expect(wrapper.findComponent(CardHeader).props('title')).toBe(title)
      expect(wrapper.get('button[type="submit"]').text()).toBe(i18n.global.t('views.storage.cleanup.submit'))
      expect(wrapper.text()).toContain(i18n.global.t('views.storage.cleanup.minimumUnusedMinutes'))
      expect(wrapper.text()).toContain(i18n.global.t('views.storage.cleanup.scope'))
      for (const category of categories)
      {
        expect(wrapper.text()).toContain(i18n.global.t(`views.storage.cleanup.categoryLabels.${category}`))
      }
      expect(wrapper.findAll('option').map(option => option.text())).toEqual(
        ['all', 'hour', 'day', 'week', 'month'].map(option => i18n.global.t(`views.storage.cleanup.timeOptions.${option}`)))
    }

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    for (const language of ['zh-CN', 'en-US', 'ja-JP'] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.get('[role="status"]').text()).toContain(i18n.global.t('views.storage.cleanup.completed', { files: '6', bytes: '3.5 KB' }))
      expect(wrapper.get('button').text()).toBe(i18n.global.t('views.common.done'))
    }
  })
})
