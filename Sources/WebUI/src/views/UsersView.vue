<template>
  <PageHeader :title="t('views.users.title')"><v-btn v-if="tab === 'invitations'" color="primary" prepend-icon="mdi-plus" @click="createOpen = true">{{ t('views.users.createInvitation') }}</v-btn></PageHeader>
  <v-tabs v-model="tab" color="primary" class="mb-6"><v-tab value="users">{{ t('views.users.users') }}<v-chip v-if="pendingCount" color="primary" class="ml-2" size="x-small">{{ formatNumber(pendingCount) }}</v-chip></v-tab><v-tab value="invitations">{{ t('views.users.invitations') }}</v-tab></v-tabs>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <template v-if="tab === 'users'">
    <div class="filter-bar"><v-text-field v-model="search" :label="t('views.users.search')" prepend-inner-icon="mdi-magnify" clearable density="compact" style="max-width: 330px" /><v-chip-group v-model="userFilter" mandatory color="primary"><v-chip value="all">{{ t('views.common.all') }}</v-chip><v-chip value="Pending">{{ t('views.common.pending') }}</v-chip><v-chip value="Active">{{ t('views.common.approved') }}</v-chip><v-chip value="Disabled">{{ t('accountManagement.banned') }}</v-chip></v-chip-group></div>
    <v-card v-if="visibleUsers.length"><v-table><thead><tr><th>{{ t('views.common.user') }}</th><th>{{ t('views.common.email') }}</th><th>{{ t('views.users.role') }}</th><th>{{ t('views.common.status') }}</th><th /></tr></thead><tbody><tr v-for="user in visibleUsers" :key="user.id"><td class="py-5"><div class="d-flex align-center ga-3"><UserAvatar :name="user.displayName" :avatar-url="user.avatarUrl" size="34" />{{ user.displayName }}</div></td><td class="text-body-2 muted">{{ user.email }}</td><td><v-chip size="small" :color="user.role === 'Admin' ? 'primary' : undefined">{{ user.role === 'Admin' ? t('views.common.admin') : t('views.common.user') }}</v-chip></td><td><v-chip v-if="user.status === 'Disabled'" size="small" color="error">{{ t('accountManagement.banned') }}</v-chip><StatusChip v-else :status="user.status === 'Active' ? 'approved' : user.status" /></td><td><div class="action-buttons"><v-btn v-if="user.role === 'User'" size="small" variant="text" :disabled="busy" @click="editPermissions(user)">{{ t('shell.permissions.title') }}</v-btn><template v-if="user.status === 'Pending'"><v-btn size="small" color="primary" variant="text" :disabled="busy" @click="approve(user, true)">{{ t('views.users.approve') }}</v-btn><v-btn size="small" color="error" variant="text" :disabled="busy" @click="approve(user, false)">{{ t('views.users.reject') }}</v-btn></template><v-menu v-else-if="user.id !== auth.user?.id"><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-dots-horizontal" variant="text" size="small" :aria-label="t('views.users.actions')" /></template><v-list density="compact"><v-list-item :title="user.role === 'Admin' ? t('views.users.makeUser') : t('views.users.makeAdmin')" @click="roleUser = user" /><v-list-item v-if="user.status === 'Active' || user.status === 'Disabled'" :title="t(user.status === 'Disabled' ? 'accountManagement.unban' : 'accountManagement.ban')" :disabled="statusBusy" @click="selectStatusUser(user)" /></v-list></v-menu></div></td></tr></tbody></v-table></v-card>
    <EmptyState v-else-if="!busy" :title="t('views.users.empty')" icon="mdi-account-group-outline" />
  </template>
  <template v-else>
    <v-alert v-if="createdCode" type="success" class="mb-6"><div class="d-flex align-center justify-space-between ga-3"><span class="mono">{{ createdCode }}</span><v-btn variant="text" size="small" prepend-icon="mdi-content-copy" @click="copyCode">{{ t('views.users.copy') }}</v-btn></div></v-alert>
    <v-card v-if="invitations.length"><v-table><thead><tr><th>{{ t('views.common.createdAt') }}</th><th>{{ t('views.users.used') }}</th><th>{{ t('views.common.expiresAt') }}</th><th>{{ t('views.common.status') }}</th><th /></tr></thead><tbody><tr v-for="invitation in invitations" :key="invitation.id"><td class="py-5 text-caption">{{ formatDate(invitation.createdAtUtc) }}</td><td class="mono text-caption">{{ formatNumber(invitation.usedCount) }} / {{ formatNumber(invitation.maxUses) }}</td><td class="text-caption">{{ formatDate(invitation.expiresAtUtc) }}</td><td><StatusChip :status="invitation.revoked ? 'disabled' : invitation.expiresAtUtc && new Date(invitation.expiresAtUtc).getTime() <= Date.now() ? 'expired' : 'valid'" /></td><td><v-btn v-if="!invitation.revoked" size="small" variant="text" color="error" :disabled="busy" @click="revoke(invitation)">{{ t('views.users.revoke') }}</v-btn></td></tr></tbody></v-table></v-card>
    <EmptyState v-else-if="!busy" :title="t('views.users.noInvitations')" icon="mdi-ticket-confirmation-outline" />
  </template>
  <v-dialog v-model="createOpen" max-width="440" :aria-label="t('views.users.createInvitation')"><ContentCard :title="t('views.users.createInvitation')"><form class="form-stack" @submit.prevent="create"><v-text-field v-model.number="maxUses" :label="t('views.users.maxUses')" type="number" min="1" max="10000" required /><v-text-field v-model="expiry" :label="t('views.common.expiresAt')" type="datetime-local" /><v-alert v-if="createError" type="error">{{ createError }}</v-alert><CardActions embedded><v-btn variant="text" @click="createOpen = false">{{ t('views.common.cancel') }}</v-btn><v-btn type="submit" color="primary" :loading="createBusy">{{ t('views.users.create') }}</v-btn></CardActions></form></ContentCard></v-dialog>
  <ConfirmDialog :model-value="!!roleUser" :title="roleUser?.role === 'Admin' ? t('views.users.confirmUser') : t('views.users.confirmAdmin')" :message="roleUser?.displayName" :busy="busy" :error="error" @update:model-value="!$event && (roleUser = undefined)" @confirm="changeRole" />
  <ConfirmDialog :model-value="!!statusUser" :title="t(statusUser?.status === 'Disabled' ? 'accountManagement.unbanConfirm' : 'accountManagement.banConfirm')" :message="t(statusUser?.status === 'Disabled' ? 'accountManagement.unbanMessage' : 'accountManagement.banMessage', { name: statusUser?.displayName ?? '' })" :action-label="t(statusUser?.status === 'Disabled' ? 'accountManagement.unban' : 'accountManagement.ban')" :busy="statusBusy" :error="statusError" @update:model-value="!$event && (statusUser = undefined)" @confirm="changeStatus" />
  <v-dialog :model-value="!!permissionUser" max-width="520" scrollable :persistent="permissionsBusy" :aria-label="t('shell.permissions.title')" @update:model-value="!$event && (permissionUser = undefined)">
    <ContentCard :title="t('shell.permissions.title')">
      <template #suffix><span class="permission-subject muted">{{ permissionUser?.displayName }}</span></template>
      <template #header-actions><v-btn icon="mdi-close" variant="text" size="small" :disabled="permissionsBusy" :aria-label="t('views.common.cancel')" @click="permissionUser = undefined" /></template>
      <PermissionFields v-model="permissionDraft" :definitions="auth.permissionDefinitions" :disabled="permissionsBusy" />
      <v-alert v-if="permissionsError" type="error" density="compact" class="mt-3">{{ permissionsError }}</v-alert>
      <template #actions><v-btn variant="text" :disabled="permissionsBusy" @click="permissionUser = undefined">{{ t('views.common.cancel') }}</v-btn><v-btn color="primary" :loading="permissionsBusy" @click="savePermissions">{{ t('shell.permissions.save') }}</v-btn></template>
    </ContentCard>
  </v-dialog>
  <v-snackbar v-model="copied">{{ t('views.users.copied') }}</v-snackbar>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import CardActions from '@/components/common/CardActions.vue'
import { computed, ref } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { Invitation, UserPermissions, UserProfile } from '@/models/auth'
import { formatDate, formatNumber } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import PermissionFields from '@/components/common/PermissionFields.vue'

const { t } = useI18n()

const auth = useAuthStore()
const tab = ref('users')
const users = ref<UserProfile[]>([])
const invitations = ref<Invitation[]>([])
const search = ref('')
const userFilter = ref('all')
const roleUser = ref<UserProfile>()
const statusUser = ref<UserProfile>()
const { busy: statusBusy, error: statusError, run: runStatus } = useAction()
let usersRevision = 0
const permissionUser = ref<UserProfile>()
const permissionDraft = ref<UserPermissions>([])
const { busy: permissionsBusy, error: permissionsError, run: runPermissions } = useAction()
const createOpen = ref(false)
const createdCode = ref('')
const maxUses = ref(1)
const expiry = ref('')
const copied = ref(false)
const { busy, error, run } = useAction()
const { busy: createBusy, error: createError, run: runCreate } = useAction()
const pendingCount = computed(() => users.value.filter(user => user.status === 'Pending').length)
const visibleUsers = computed(() => users.value.filter(user => (userFilter.value === 'all' || user.status === userFilter.value) && `${user.displayName} ${user.email}`.toLowerCase().includes(search.value.toLowerCase())))
usePolling(async () =>
{
  const revision = usersRevision
  await run(async () =>
  {
    const [loadedUsers, loadedInvitations] = await Promise.all([AuthAPI.users(), AuthAPI.invitations()])
    if (revision === usersRevision) { users.value = loadedUsers }
    invitations.value = loadedInvitations
  })
}, 15000)
function selectStatusUser(user: UserProfile): void
{
  statusUser.value = user
  statusError.value = ''
}
async function changeStatus(): Promise<void>
{
  const user = statusUser.value
  if (!user || statusBusy.value || user.id === auth.user?.id) { return }
  await runStatus(async () =>
  {
    const updated = await (user.status === 'Disabled' ? AuthAPI.unbanUser(user.id) : AuthAPI.banUser(user.id))
    usersRevision++
    users.value = users.value.map(item => item.id === updated.id ? updated : item)
    statusUser.value = undefined
  })
}
function editPermissions(user: UserProfile): void
{
  permissionUser.value = user
  permissionDraft.value = [...user.permissions]
  permissionsError.value = ''
}
async function savePermissions(): Promise<void>
{
  const user = permissionUser.value
  if (!user || permissionsBusy.value) { return }
  await runPermissions(async () =>
  {
    const updated = await AuthAPI.userPermissions(user.id, permissionDraft.value)
    users.value = users.value.map(item => item.id === updated.id ? updated : item)
    permissionUser.value = undefined
  })
}
async function approve(user: UserProfile, approved: boolean): Promise<void>
{
  await run(async () => { await AuthAPI.approveUser(user.id, approved); users.value = await AuthAPI.users() })
}
async function changeRole(): Promise<void>
{
  if (!roleUser.value) { return }
  await run(async () => { await AuthAPI.userRole(roleUser.value!.id, roleUser.value!.role === 'Admin' ? 'User' : 'Admin'); roleUser.value = undefined; users.value = await AuthAPI.users() })
}
async function create(): Promise<void>
{
  await runCreate(async () =>
  {
    const result = await AuthAPI.createInvitation(maxUses.value, expiry.value ? new Date(expiry.value).toISOString() : null)
    createdCode.value = result.code ?? ''
    createOpen.value = false
    invitations.value = await AuthAPI.invitations()
  })
}
async function revoke(invitation: Invitation): Promise<void>
{
  await run(async () => { await AuthAPI.revokeInvitation(invitation.id); invitations.value = await AuthAPI.invitations() })
}
async function copyCode(): Promise<void>
{
  await run(async () => { await navigator.clipboard.writeText(createdCode.value); copied.value = true })
}
</script>

<style scoped>
.permission-subject { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: min(240px, 50vw); font-size: .8125rem; }
</style>
