// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, disposePinia } from 'pinia'
import AvatarCropDialog from '../src/components/account/AvatarCropDialog.vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import type { UserProfile } from '../src/models/auth'

afterEach(() =>
{
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

it('crops the selected image in square coordinates and uploads a JPEG', async () =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  const bitmap = { width: 640, height: 320, close: vi.fn() }
  vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue(bitmap))
  const drawImage = vi.fn()
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
    drawImage, fillRect: vi.fn(),
  } as unknown as CanvasRenderingContext2D)
  const toBlob = vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(callback =>
    callback(new Blob(['cropped'], { type: 'image/jpeg' })))
  const profile: UserProfile = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', permissions: [], role: 'User', status: 'Active' }
  const avatarUrl = '/api/auth/avatars/viewer?v=custom'
  const upload = vi.spyOn(AuthAPI, 'uploadAvatar').mockResolvedValue({ ...profile, avatarUrl })
  const pinia = createPinia()
  useAuthStore(pinia).user = profile
  const wrapper = mount(AvatarCropDialog, {
    props: { modelValue: true },
    global: {
      plugins: [pinia, i18n],
      stubs: {
        VDialog: { template: '<div><slot/></div>' },
        ContentCard: { template: '<div><slot/><slot name="actions"/></div>' },
        VFileInput: true, VSlider: true, VBtn: true, VAlert: true,
      },
    },
  })
  try
  {
    wrapper.findComponent({ name: 'VFileInput' }).vm.$emit('update:modelValue', new File(['image'], 'avatar.png', { type: 'image/png' }))
    await flushPromises()
    expect(drawImage).toHaveBeenLastCalledWith(bitmap, 160, 0, 320, 320, 0, 0, 256, 256)

    wrapper.findAllComponents({ name: 'VSlider' })[0]!.vm.$emit('update:modelValue', 2)
    await flushPromises()
    expect(drawImage).toHaveBeenLastCalledWith(bitmap, 240, 80, 160, 160, 0, 0, 256, 256)
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(toBlob).toHaveBeenCalledWith(expect.any(Function), 'image/jpeg', 0.88)
    expect(upload).toHaveBeenCalledOnce()
    expect(useAuthStore(pinia).user?.avatarUrl).toBe(avatarUrl)
  }
  finally
  {
    wrapper.unmount()
    disposePinia(pinia)
  }
  expect(bitmap.close).toHaveBeenCalledOnce()
})
