<template>
  <PageHeader title="用户与邀请"><v-btn v-if="tab === 'invitations'" color="primary" prepend-icon="mdi-plus" @click="createOpen = true">创建邀请码</v-btn></PageHeader>
  <v-tabs v-model="tab" color="primary" class="mb-6"><v-tab value="users">用户<v-chip v-if="pendingCount" color="primary" class="ml-2" size="x-small">{{ pendingCount }}</v-chip></v-tab><v-tab value="invitations">邀请码</v-tab></v-tabs>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <template v-if="tab === 'users'">
    <div class="filter-bar"><v-text-field v-model="search" label="搜索用户" prepend-inner-icon="mdi-magnify" clearable density="compact" style="max-width: 330px" /><v-chip-group v-model="userFilter" mandatory color="primary"><v-chip value="all">全部</v-chip><v-chip value="Pending">待审核</v-chip><v-chip value="Active">已通过</v-chip></v-chip-group></div>
    <v-card v-if="visibleUsers.length"><v-table><thead><tr><th>用户</th><th>邮箱</th><th>角色</th><th>状态</th><th /></tr></thead><tbody><tr v-for="user in visibleUsers" :key="user.id"><td class="py-5"><div class="d-flex align-center ga-3"><v-avatar size="34" color="surface-variant"><v-img v-if="user.avatarUrl" :src="user.avatarUrl" /><span v-else>{{ user.displayName.slice(0, 1) }}</span></v-avatar>{{ user.displayName }}</div></td><td class="text-body-2 muted">{{ user.email }}</td><td><v-chip size="small" :color="user.role === 'Admin' ? 'primary' : undefined">{{ user.role === 'Admin' ? '管理员' : '用户' }}</v-chip></td><td><StatusChip :status="user.status === 'Active' ? 'approved' : user.status" /></td><td><div class="action-buttons"><template v-if="user.status === 'Pending'"><v-btn size="small" color="primary" variant="text" :disabled="busy" @click="approve(user, true)">通过</v-btn><v-btn size="small" color="error" variant="text" :disabled="busy" @click="approve(user, false)">拒绝</v-btn></template><v-menu v-else-if="user.id !== auth.user?.id"><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-dots-horizontal" variant="text" size="small" aria-label="用户操作" /></template><v-list density="compact"><v-list-item :title="user.role === 'Admin' ? '设为用户' : '设为管理员'" @click="roleUser = user" /></v-list></v-menu></div></td></tr></tbody></v-table></v-card>
    <EmptyState v-else-if="!busy" title="暂无匹配用户" icon="mdi-account-group-outline" />
  </template>
  <template v-else>
    <v-alert v-if="createdCode" type="success" class="mb-6"><div class="d-flex align-center justify-space-between ga-3"><span class="mono">{{ createdCode }}</span><v-btn variant="text" size="small" prepend-icon="mdi-content-copy" @click="copyCode">复制</v-btn></div></v-alert>
    <v-card v-if="invitations.length"><v-table><thead><tr><th>创建时间</th><th>已使用</th><th>到期时间</th><th>状态</th><th /></tr></thead><tbody><tr v-for="invitation in invitations" :key="invitation.id"><td class="py-5 text-caption">{{ formatDate(invitation.createdAtUtc) }}</td><td class="mono text-caption">{{ invitation.usedCount }} / {{ invitation.maxUses }}</td><td class="text-caption">{{ formatDate(invitation.expiresAtUtc) }}</td><td><StatusChip :status="invitation.revoked ? 'disabled' : invitation.expiresAtUtc && new Date(invitation.expiresAtUtc).getTime() <= Date.now() ? 'expired' : 'valid'" /></td><td><v-btn v-if="!invitation.revoked" size="small" variant="text" color="error" :disabled="busy" @click="revoke(invitation)">撤销</v-btn></td></tr></tbody></v-table></v-card>
    <EmptyState v-else-if="!busy" title="暂无邀请码" icon="mdi-ticket-confirmation-outline" />
  </template>
  <v-dialog v-model="createOpen" max-width="440"><v-card class="pa-6"><h2 class="text-h6 mb-6">创建邀请码</h2><form class="form-stack" @submit.prevent="create"><v-text-field v-model.number="maxUses" label="可用次数" type="number" min="1" max="10000" required /><v-text-field v-model="expiry" label="到期时间" type="datetime-local" /><v-alert v-if="createError" type="error">{{ createError }}</v-alert><div class="d-flex justify-end ga-3"><v-btn variant="text" @click="createOpen = false">取消</v-btn><v-btn type="submit" color="primary" :loading="createBusy">创建</v-btn></div></form></v-card></v-dialog>
  <ConfirmDialog :model-value="!!roleUser" :title="roleUser?.role === 'Admin' ? '设为普通用户？' : '设为管理员？'" :message="roleUser?.displayName" :busy="busy" :error="error" @update:model-value="!$event && (roleUser = undefined)" @confirm="changeRole" />
  <v-snackbar v-model="copied">已复制邀请码</v-snackbar>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { Invitation, UserProfile } from '@/models/auth'
import { formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'

const auth = useAuthStore()
const tab = ref('users')
const users = ref<UserProfile[]>([])
const invitations = ref<Invitation[]>([])
const search = ref('')
const userFilter = ref('all')
const roleUser = ref<UserProfile>()
const createOpen = ref(false)
const createdCode = ref('')
const maxUses = ref(1)
const expiry = ref('')
const copied = ref(false)
const { busy, error, run } = useAction()
const { busy: createBusy, error: createError, run: runCreate } = useAction()
const pendingCount = computed(() => users.value.filter(user => user.status === 'Pending').length)
const visibleUsers = computed(() => users.value.filter(user => (userFilter.value === 'all' || user.status === userFilter.value) && `${user.displayName} ${user.email}`.toLowerCase().includes(search.value.toLowerCase())))
usePolling(async () => { await run(async () => { [users.value, invitations.value] = await Promise.all([AuthAPI.users(), AuthAPI.invitations()]) }) }, 15000)
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
