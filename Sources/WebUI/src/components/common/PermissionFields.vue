<template>
  <div class="permission-fields">
    <section v-for="group in groups" :key="group.id" class="permission-group">
      <h3 class="permission-group-title muted">{{ t(`shell.permissions.groups.${group.id}`) }}</h3>
      <div v-for="item in group.items" :key="item.id" class="permission-option"
           :title="item.descriptionKey ? t(item.descriptionKey) : undefined">
        <v-icon :icon="item.icon" :color="item.isDangerous ? 'error' : 'primary'" size="20" class="permission-icon"/>
        <v-switch class="permission-switch" :model-value="permissions.includes(item.id)" :disabled="disabled"
                  :label="t(item.labelKey)" :aria-label="t(item.labelKey)"
                  :color="item.isDangerous ? 'error' : 'primary'" density="compact" inset hide-details
                  @update:model-value="setPermission(item.id, $event)"/>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import type {PermissionDefinition, UserPermissions} from '@/models/auth'

const props = defineProps<{ definitions: readonly PermissionDefinition[]; disabled?: boolean }>()
const permissions = defineModel<UserPermissions>({required: true})
const {t} = useI18n()
const groups = computed(() => Array.from(new Set(props.definitions.map(item => item.group))).map(id => ({
  id,
  items: props.definitions.filter(item => item.group === id)
})))

function setPermission(id: string, value: unknown): void {
  permissions.value = value === true ? [...new Set([...permissions.value, id])] : permissions.value.filter(item => item !== id)
}
</script>

<style scoped>
.permission-fields {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.permission-group {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.permission-group-title {
  margin: 0;
  font-size: .75rem;
  font-weight: 500;
}

.permission-option {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 4px 12px;
  border: 1px solid rgba(var(--v-theme-on-surface), .07);
  border-radius: 10px;
  background: rgba(var(--v-theme-on-surface), .025);
}

.permission-icon {
  flex-shrink: 0;
}

.permission-switch {
  flex: 1;
  min-width: 0;
}

.permission-switch :deep(.v-selection-control) {
  flex-direction: row-reverse;
  gap: 12px;
}

.permission-switch :deep(.v-label) {
  flex: 1;
  max-width: none;
  opacity: 1;
  font-size: .875rem;
}

.permission-switch :deep(.v-selection-control__wrapper) {
  flex-shrink: 0;
}
</style>
