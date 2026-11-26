// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, type Pinia } from 'pinia'
import { AuthAPI } from '../src/apis/AuthAPI'
import AccountProfileCard from '../src/components/account/AccountProfileCard.vue'
import type { UserProfile } from '../src/models/auth'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'

const profile: UserProfile = {
  id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: [],
  avatarUrl: '/api/auth/avatars/viewer?v=current',
}
let wrapper: VueWrapper | undefined
let pinia: Pinia | undefined

function mountProfile()
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  i18n.global.locale.value = 'zh-CN'
  pinia = createPinia()
  const auth = useAuthStore(pinia)
  auth.user = { ...profile }
  wrapper = mount(AccountProfileCard, {
    attachTo: document.body,
    global: {
      plugins: [pinia, i18n],
      stubs: {
        ContentCard: { template: '<section><slot/><slot name="actions"/></section>' },
        VAlert: { template: '<div role="alert"><slot/></div>' },
        VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot/></div>' },
        VBtn: { props: ['disabled', 'icon', 'loading'], template: '<button :disabled="disabled || loading" :data-icon="icon"><slot/></button>' },
        VTextField: {
          props: ['modelValue', 'label', 'disabled'],
          template: `<input :value="modelValue" :aria-label="label" :disabled="disabled" @input="$emit('update:modelValue', $event.target.value)" />`,
        },
        VDialog: {
          props: ['modelValue'],
          template: `<div><slot name="activator" :props="{ onClick: () => $emit('update:modelValue', true) }"/><div v-if="modelValue" role="dialog"><slot/></div></div>`,
        },
        UserAvatar: true, VIcon: true, VFileInput: true,
      },
    },
  })
  return { wrapper, auth }
}

afterEach(() =>
{
  wrapper?.unmount()
  if (pinia) { disposePinia(pinia) }
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

it('replaces the edit icon with native save and preserves the inline draft on failure', async () =>
{
  const { wrapper } = mountProfile()
  const saveName = vi.spyOn(AuthAPI, 'setDisplayName').mockRejectedValueOnce(new Error('保存失败'))
    .mockResolvedValueOnce({ ...profile, displayName: '新名称' })
  expect(wrapper.get('.profile-name').text()).toBe('观众')
  expect(wrapper.find('input').exists()).toBe(false)
  await wrapper.get('button[aria-label="修改显示名称"]').trigger('click')
  const input = wrapper.get<HTMLInputElement>('input[aria-label="显示名称"]')
  expect(input.element.value).toBe('观众')
  await input.setValue(' 新名称 ')

  const saveButton = wrapper.get('button[type="submit"][aria-label="保存"]')
  expect(saveButton.attributes('data-icon')).toBe('mdi-content-save-outline')
  await saveButton.trigger('click')
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
  expect(input.element.value).toBe(' 新名称 ')

  await saveButton.trigger('click')
  await flushPromises()
  expect(saveName).toHaveBeenLastCalledWith('新名称')
  expect(wrapper.get('.profile-name').text()).toBe('新名称')
  expect(wrapper.find('input').exists()).toBe(false)
  expect(document.activeElement).toBe(wrapper.get('button[aria-label="修改显示名称"]').element)
})

it('confirms avatar removal inside the crop dialog launched by the avatar', async () =>
{
  const { wrapper, auth } = mountProfile()
  const clear = vi.spyOn(AuthAPI, 'clearAvatar').mockResolvedValue({ ...profile, avatarUrl: '' })
  expect(wrapper.text()).not.toContain('清空头像')
  await wrapper.get('button[aria-label="设置头像"]').trigger('click')
  const dialog = wrapper.get('[role="dialog"]')
  const clearButton = () => dialog.findAll('button').find(button => button.text() === '清空头像')!
  await clearButton().trigger('click')
  expect(dialog.text()).toContain('清空后将显示用户名首字母。')
  expect(clear).not.toHaveBeenCalled()

  await clearButton().trigger('click')
  await flushPromises()
  expect(clear).toHaveBeenCalledOnce()
  expect(auth.user?.avatarUrl).toBe('')
  expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  expect(wrapper.get('[role="status"]').text()).toBe('头像已清空')
})
