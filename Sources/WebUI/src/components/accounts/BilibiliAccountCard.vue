<template>
  <v-card class="account-card">
    <div class="account-card-header">
      <v-avatar size="56" color="surface-variant" class="account-card-avatar" aria-hidden="true">
        <v-icon icon="mdi-account-outline" size="28" color="secondary" />
        <img
          v-if="account.avatarUrl && !avatarFailed"
          :key="account.avatarUrl"
          :src="AuthAPI.bilibiliAccountAvatar(account.id, account.avatarUrl)"
          alt=""
          referrerpolicy="no-referrer"
          class="account-card-avatar-image"
          @error="avatarFailed = true"
        />
      </v-avatar>
      <div class="account-card-identity">
        <h2 class="account-card-name" :title="account.userName">{{ account.userName }}</h2>
        <div class="account-card-badges">
          <StatusChip :status="account.status" />
          <v-chip v-if="account.isDefault" size="small" color="primary">{{ t('accountManagement.default') }}</v-chip>
          <v-chip v-if="account.isEffectiveDefault && !account.isDefault" size="small" color="info">{{ t('accountManagement.effective') }}</v-chip>
        </div>
      </div>
      <v-menu>
        <template #activator="{ props: menuProps }">
          <v-btn v-bind="menuProps" icon="mdi-dots-horizontal" size="small" variant="text" :aria-label="t('views.accounts.actions')" />
        </template>
        <v-list density="compact">
          <v-list-item v-if="account.isDefault || account.status === 'Valid'" :title="t(account.isDefault ? 'accountManagement.clearDefault' : 'accountManagement.setDefault')" prepend-icon="mdi-star-outline" :disabled="busy" @click="emit('setDefault', account.isDefault ? null : account.id)" />
          <v-list-item :title="t('accountManagement.relogin')" prepend-icon="mdi-login" :disabled="busy" @click="emit('relogin', account)" />
          <v-list-item :title="t('views.accounts.remove')" prepend-icon="mdi-delete-outline" :disabled="busy" @click="emit('remove', account)" />
        </v-list>
      </v-menu>
    </div>
    <div class="account-card-uid text-caption muted mono">UID {{ account.uid }}</div>
    <v-btn v-if="account.status === 'Expired'" color="primary" variant="tonal" prepend-icon="mdi-login" class="mb-4" :disabled="busy" @click="emit('relogin', account)">{{ t('accountManagement.relogin') }}</v-btn>
    <div class="account-card-validation text-caption muted">
      <span class="account-card-validation-label">
        <v-icon icon="mdi-clock-check-outline" size="16" />
        {{ t('views.accounts.lastValidated') }}
      </span>
      <time v-if="account.lastValidatedAtUtc" :datetime="account.lastValidatedAtUtc">{{ formatDate(account.lastValidatedAtUtc) }}</time>
      <span v-else>{{ t('views.accounts.notValidated') }}</span>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AuthAPI } from '@/apis/AuthAPI'
import type { BilibiliAccount } from '@/models/auth'
import { formatDate } from '@/utilities/format'
import StatusChip from '@/components/common/StatusChip.vue'

const { t } = useI18n()
const props = defineProps<{ account: BilibiliAccount; busy?: boolean }>()
const emit = defineEmits<{ remove: [account: BilibiliAccount]; relogin: [account: BilibiliAccount]; setDefault: [accountId: string | null] }>()
const avatarFailed = ref(false)

watch(() => props.account.avatarUrl, () =>
{
  avatarFailed.value = false
})
</script>

<style scoped>
.account-card
{
  display: flex;
  flex-direction: column;
  min-width: 0;
  padding: 20px;
}
.account-card-header
{
  display: grid;
  grid-template-columns: 56px minmax(0, 1fr) auto;
  align-items: center;
  gap: 14px;
}
.account-card-avatar-image
{
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: inherit;
}
.account-card-identity
{
  min-width: 0;
}
.account-card-name
{
  margin: 0;
  overflow: hidden;
  font-size: 1rem;
  font-weight: 600;
  line-height: 1.5;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.account-card-badges
{
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
}
.account-card-uid
{
  margin-top: 14px;
  margin-bottom: 18px;
  overflow-wrap: anywhere;
}
.account-card-validation
{
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px 12px;
  margin-top: auto;
  padding-top: 14px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), .07);
}
.account-card-validation-label
{
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.account-card-validation time
{
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
@media (max-width: 600px)
{
  .account-card
  {
    padding: 18px;
  }
  .account-card-header
  {
    gap: 12px;
  }
}
</style>
