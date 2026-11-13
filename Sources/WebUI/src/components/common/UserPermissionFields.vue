<template>
  <div class="permission-fields">
    <div v-for="item in items" :key="item.key" class="permission-option" :title="item.description ? t(item.description) : undefined">
      <v-icon :icon="item.icon" :color="item.key === 'purgeRecordings' ? 'error' : 'primary'" size="20" class="permission-icon" />
      <v-switch class="permission-switch" :model-value="permissions[item.key]" :disabled="disabled" :label="t(item.label)" :aria-label="t(item.label)" :color="item.key === 'purgeRecordings' ? 'error' : 'primary'" density="compact" inset hide-details @update:model-value="setPermission(item.key, $event)" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UserPermissions } from '@/models/auth'

defineProps<{ disabled?: boolean }>()
const permissions = defineModel<UserPermissions>({ required: true })
const { t } = useI18n()
const items = [
  { key: 'manageRecordings', label: 'shell.permissions.manageRecordings', description: 'shell.permissions.manageDescription', icon: 'mdi-shield-check-outline' },
  { key: 'viewAnalytics', label: 'shell.permissions.viewAnalytics', description: null, icon: 'mdi-chart-timeline-variant' },
  { key: 'purgeRecordings', label: 'shell.permissions.purgeRecordings', description: 'shell.permissions.purgeDescription', icon: 'mdi-delete-forever-outline' },
] as const

function setPermission(key: keyof UserPermissions, value: unknown): void
{
  permissions.value = { ...permissions.value, [key]: value === true }
}
</script>

<style scoped>
.permission-fields { display: flex; flex-direction: column; gap: 8px; }
.permission-option { display: flex; align-items: center; gap: 12px; padding: 4px 12px; border: 1px solid rgba(var(--v-theme-on-surface), .07); border-radius: 10px; background: rgba(var(--v-theme-on-surface), .025); }
.permission-icon { flex-shrink: 0; }
.permission-switch { flex: 1; min-width: 0; }
.permission-switch :deep(.v-selection-control) { flex-direction: row-reverse; gap: 12px; }
.permission-switch :deep(.v-label) { flex: 1; max-width: none; opacity: 1; font-size: .875rem; }
.permission-switch :deep(.v-selection-control__wrapper) { flex-shrink: 0; }
</style>
