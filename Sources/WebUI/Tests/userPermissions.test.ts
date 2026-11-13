// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import type { TrashEntry } from '../src/models/media'
import { useAuthStore } from '../src/stores/auth'
import type { PermissionDefinition, UserPermissions, UserProfile } from '../src/models/auth'
import type { ServerSettings } from '../src/models/settings'
import { i18n } from '../src/plugins/i18n'
import PermissionFields from '../src/components/common/PermissionFields.vue'
import RegistrationSettings from '../src/components/common/RegistrationSettings.vue'
import UsersView from '../src/views/UsersView.vue'
import TrashView from '../src/views/TrashView.vue'

const denied: UserPermissions = []
const definitions: PermissionDefinition[] = [
  ['recordings.view', 'recordings', 'viewRecordings'],
  ['recordings.download', 'recordings', 'downloadRecordings'],
  ['clips.create', 'recordings', 'createClips'],
  ['recordings.manage', 'recordings', 'manageRecordings'],
  ['recordings.purge', 'recordings', 'purgeRecordings'],
  ['live-markers.upload', 'liveMarkers', 'uploadLiveMarkers'],
  ['live-markers.manage', 'liveMarkers', 'manageLiveMarkers'],
  ['analytics.view', 'analytics', 'viewAnalytics'],
].map(([id, group, label]) => ({ id: id!, group: group!, labelKey: `shell.permissions.${label}`, descriptionKey: null, icon: 'mdi-check', isDangerous: id === 'recordings.purge' || id === 'live-markers.manage', defaultGranted: false }))
const mounted: VueWrapper[] = []
let pinia: Pinia
const container = { template: '<div><slot /></div>' }
const switchField = defineComponent({
  props: ['modelValue', 'label', 'disabled'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      type: 'checkbox', 'aria-label': props.label, checked: props.modelValue, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).checked),
    })])
  },
})
const stubs = {
  VSwitch: switchField, VCard: container, VTable: container, VTabs: container, VTab: container,
  VChip: container, VChipGroup: container, VAvatar: container, VIcon: true, VDivider: true,
  VTextField: true, VSnackbar: true, VMenu: true,
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
}

function user(permissions: UserPermissions = denied): UserProfile
{
  return { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: [...permissions] }
}

function button(wrapper: VueWrapper, label: string)
{
  const result = wrapper.findAll('button').find(item => item.text() === label)
  if (!result) { throw new Error(`Missing button: ${label}`) }
  return result
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  useAuthStore().permissionDefinitions = definitions
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('独立权限与管理入口', () =>
{
  it.each(Array.from({ length: 8 }, (_, value) => [value] as const))('普通用户权限组合 %i 互不提升', value =>
  {
    const auth = useAuthStore()
    const permissions = ['recordings.view', ...['recordings.manage', 'analytics.view', 'recordings.purge'].filter((_, index) => value & (1 << index))]
    auth.user = user([...permissions])
    expect(auth.canManageRecordings).toBe(permissions.includes('recordings.manage'))
    expect(auth.canViewAnalytics).toBe(permissions.includes('analytics.view'))
    expect(auth.canPurgeRecordings).toBe(permissions.includes('recordings.purge'))
    expect(auth.isAdmin).toBe(false)
  })

  it('管理员始终全权，账号失效或登出后权限全部失效', () =>
  {
    const auth = useAuthStore()
    auth.user = { ...user(), role: 'Admin' }
    expect([auth.canManageRecordings, auth.canViewAnalytics, auth.canPurgeRecordings]).toEqual([true, true, true])
    auth.user = { ...auth.user, status: 'Disabled' }
    expect([auth.canManageRecordings, auth.canViewAnalytics, auth.canPurgeRecordings]).toEqual([false, false, false])
    auth.user = null
    expect([auth.canManageRecordings, auth.canViewAnalytics, auth.canPurgeRecordings]).toEqual([false, false, false])
  })

  it('新注册默认权限与单用户编辑复用同一控件，并以快照发出修改', async () =>
  {
    const defaults = [...denied]
    const settings = {
      registrationEnabled: true, registrationApprovalRequired: true, registrationInvitationRequired: true,
      registrationEmailVerificationRequired: true, defaultUserPermissions: defaults,
    } as ServerSettings
    const wrapper = mount(RegistrationSettings, { props: { modelValue: settings }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.findComponent(PermissionFields).exists()).toBe(true)
    await wrapper.get('input[aria-label="查看分析数据"]').setValue(true)
    expect(settings.defaultUserPermissions).toEqual(['analytics.view'])
    expect(defaults).toEqual(denied)
    expect(wrapper.text()).toContain('新注册用户默认权限')
  })

  it.each([
    [['recordings.manage'], true, false],
    [['recordings.purge'], false, true],
    [['recordings.manage', 'recordings.purge'], true, true],
  ] as const)('回收站恢复与彻删按钮独立授权 %j', async (permissions, restore, purge) =>
  {
    const auth = useAuthStore()
    auth.user = user([...permissions])
    const entry: TrashEntry = {
      id: 'entry', createdAtUtc: '2026-11-13T00:00:00Z', ownerId: 'other-user', kind: 'Session',
      entityId: 'recording', title: '回收录像', bytes: 42, purgeAtUtc: null, state: 'Active',
    }
    vi.spyOn(MediaAPI, 'trash').mockResolvedValue([entry])
    vi.spyOn(MediaAPI, 'storage').mockResolvedValue([])
    const wrapper = mount(TrashView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.findAll('button').some(item => item.text() === '恢复')).toBe(restore)
    expect(wrapper.find('button[aria-label="彻底删除"]').exists()).toBe(purge)
    expect(wrapper.text()).toContain('不自动删除')
  })

  it('没有观看权时下载、切片和上传均不可用，仍可清理录像', () =>
  {
    const auth = useAuthStore()
    auth.user = user(['recordings.download', 'clips.create', 'live-markers.upload', 'recordings.manage'])
    expect([auth.canViewRecordings, auth.canDownloadRecordings, auth.canCreateClips, auth.canUploadLiveMarkers]).toEqual([false, false, false, false])
    expect(auth.canManageRecordings).toBe(true)
    expect(auth.homePath).toBe('/account')
    auth.user = user(['recordings.view', 'recordings.download', 'clips.create', 'live-markers.upload'])
    expect([auth.canViewRecordings, auth.canDownloadRecordings, auth.canCreateClips, auth.canUploadLiveMarkers]).toEqual([true, true, true, true])
    expect(auth.homePath).toBe('/')
  })

  it('通用权限面板渲染传入目录并保留未显示的授权', async () =>
  {
    const wrapper = mount(PermissionFields, { props: { modelValue: ['preserved.permission'], definitions: [{ ...definitions[0]!, id: 'future.permission' }] }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await wrapper.get('input[aria-label="观看录像"]').setValue(true)
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual([['preserved.permission', 'future.permission']])
  })

  it('编辑用户不提前修改其权限，保存失败保留草稿并允许重试', async () =>
  {
    const profile = user()
    const auth = useAuthStore()
    auth.user = { ...user(), id: 'admin', role: 'Admin' }
    vi.spyOn(AuthAPI, 'users').mockResolvedValue([profile])
    vi.spyOn(AuthAPI, 'invitations').mockResolvedValue([])
    const save = vi.spyOn(AuthAPI, 'userPermissions').mockRejectedValueOnce(new Error('保存失败')).mockResolvedValue({ ...profile, permissions: ['recordings.purge'] })
    const wrapper = mount(UsersView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    await button(wrapper, '权限').trigger('click')
    await wrapper.get('input[aria-label="彻底删除录像"]').setValue(true)
    expect(profile.permissions).toEqual(denied)
    expect(save).not.toHaveBeenCalled()
    await button(wrapper, '保存').trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="彻底删除录像"]').element.checked).toBe(true)
    await button(wrapper, '保存').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenLastCalledWith('viewer', ['recordings.purge'])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })
})
