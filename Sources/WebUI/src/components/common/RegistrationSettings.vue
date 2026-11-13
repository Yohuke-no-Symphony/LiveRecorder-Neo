<template>
  <v-card class="form-section" style="max-width: 660px">
    <h2>{{ t('shell.settings.registrationSettings') }}</h2>
    <div class="form-stack">
      <v-switch v-model="settings.registrationEnabled" :disabled="disabled" :label="t('shell.settings.registrationEnabled')" />
      <v-switch v-model="settings.registrationApprovalRequired" :disabled="disabled" :label="t('shell.settings.registrationApprovalRequired')" :hint="t('shell.settings.registrationApprovalDescription')" persistent-hint />
      <v-switch v-model="settings.registrationInvitationRequired" :disabled="disabled" :label="t('shell.settings.registrationInvitationRequired')" :hint="t('shell.settings.registrationInvitationDescription')" persistent-hint />
      <v-switch v-model="settings.registrationEmailVerificationRequired" :disabled="disabled" :label="t('shell.settings.registrationEmailVerificationRequired')" :hint="t('shell.settings.registrationEmailVerificationDescription')" persistent-hint />
    </div>
    <v-divider class="my-6" />
    <v-btn variant="tonal" prepend-icon="mdi-account-group-outline" to="/admin/users">{{ t('shell.settings.manageUsers') }}</v-btn>
  </v-card>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { ServerSettings } from '@/models/settings'

defineProps<{ disabled?: boolean }>()
const settings = defineModel<ServerSettings>({ required: true })
const { t } = useI18n()
</script>
