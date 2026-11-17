<template>
  <v-chip :color="appearance.color" size="small" :prepend-icon="appearance.icon">{{ appearance.label }}</v-chip>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps<{ status: string }>()
const definitions: Record<string, { labelKey: string; color: string; icon?: string }> = {
  recording: { labelKey: 'components.status.recording', color: 'error', icon: 'mdi-record' },
  live: { labelKey: 'components.status.live', color: 'error', icon: 'mdi-record' },
  monitoring: { labelKey: 'components.status.monitoring', color: 'success' },
  waiting: { labelKey: 'components.status.waiting', color: 'warning', icon: 'mdi-timer-outline' },
  paused: { labelKey: 'components.status.paused', color: 'warning' },
  suspended: { labelKey: 'components.status.paused', color: 'warning' },
  reconnecting: { labelKey: 'components.status.reconnecting', color: 'warning' },
  error: { labelKey: 'components.status.error', color: 'error' },
  stopped: { labelKey: 'components.status.stopped', color: 'secondary' },
  offline: { labelKey: 'components.status.offline', color: 'secondary' },
  online: { labelKey: 'components.status.online', color: 'success' },
  disconnected: { labelKey: 'transcoding.offline', color: 'secondary' },
  queued: { labelKey: 'components.status.queued', color: 'warning', icon: 'mdi-timer-outline' },
  pending: { labelKey: 'components.status.pending', color: 'warning', icon: 'mdi-timer-outline' },
  running: { labelKey: 'components.status.running', color: 'info' },
  publishing: { labelKey: 'components.status.publishing', color: 'info' },
  finalizing: { labelKey: 'components.status.finalizing', color: 'info' },
  completed: { labelKey: 'components.status.completed', color: 'success' },
  succeeded: { labelKey: 'components.status.completed', color: 'success' },
  ready: { labelKey: 'components.status.ready', color: 'success' },
  skipped: { labelKey: 'transcoding.skipped', color: 'secondary' },
  failed: { labelKey: 'components.status.failed', color: 'error' },
  partial: { labelKey: 'replay.states.Partial', color: 'warning' },
  interrupted: { labelKey: 'components.status.interrupted', color: 'warning' },
  canceled: { labelKey: 'components.status.canceled', color: 'secondary' },
  cancelled: { labelKey: 'components.status.canceled', color: 'secondary' },
  approved: { labelKey: 'components.status.approved', color: 'success' },
  rejected: { labelKey: 'components.status.rejected', color: 'error' },
  disabled: { labelKey: 'components.status.disabled', color: 'secondary' },
  expired: { labelKey: 'components.status.expired', color: 'warning' },
  unavailable: { labelKey: 'components.status.unavailable', color: 'warning' },
  valid: { labelKey: 'components.status.valid', color: 'success' },
  active: { labelKey: 'components.status.valid', color: 'success' },
  invalid: { labelKey: 'components.status.invalid', color: 'error' },
  needslogin: { labelKey: 'components.status.needsLogin', color: 'warning' },
  trashed: { labelKey: 'components.status.trashed', color: 'secondary' },
}
const appearance = computed(() =>
{
  const definition = definitions[props.status.toLowerCase()]
  return definition ? { ...definition, label: t(definition.labelKey) } : { label: props.status, color: 'secondary', icon: undefined }
})
</script>
