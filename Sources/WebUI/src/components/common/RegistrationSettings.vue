<template>
  <ContentCard :title="t('shell.settings.registrationSettings')" style="max-width: 660px">
    <div class="form-stack">
      <v-switch v-model="settings.registrationEnabled" :disabled="disabled" :label="t('shell.settings.registrationEnabled')" />
      <v-switch v-model="settings.registrationApprovalRequired" :disabled="disabled" :label="t('shell.settings.registrationApprovalRequired')" />
      <v-switch v-model="settings.registrationInvitationRequired" :disabled="disabled" :label="t('shell.settings.registrationInvitationRequired')" />
      <v-switch v-model="settings.registrationEmailVerificationRequired" :disabled="disabled" :label="t('shell.settings.registrationEmailVerificationRequired')" />
    </div>
    <v-divider class="my-4" />
    <h2 class="section-heading">{{ t('shell.permissions.defaults') }}</h2>
    <p class="text-body-2 muted mb-5">{{ t('shell.permissions.defaultsDescription') }}</p>
    <PermissionFields v-model="settings.defaultUserPermissions" :definitions="auth.permissionDefinitions" :disabled="disabled" />
    <v-divider class="my-4" />
    <v-btn variant="tonal" prepend-icon="mdi-account-group-outline" to="/admin/users">{{ t('shell.settings.manageUsers') }}</v-btn>
  </ContentCard>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import ContentCard from '@/components/common/ContentCard.vue'
import type { ServerSettings } from '@/models/settings'
import PermissionFields from './PermissionFields.vue'

defineProps<{ disabled?: boolean }>()
const settings = defineModel<ServerSettings>({ required: true })
const { t } = useI18n()
const auth = useAuthStore()
</script>
