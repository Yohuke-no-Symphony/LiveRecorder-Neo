<template>
  <div class="card-header">
    <div class="card-header-title">
      <h2 :title="title">{{ title }}</h2>
      <slot name="suffix" />
    </div>
    <div v-if="$slots.actions" class="card-header-actions"><slot name="actions" /></div>
  </div>
</template>

<script setup lang="ts">
defineProps<{ title: string }>()
</script>

<style scoped>
.card-header { display: flex; align-items: center; justify-content: space-between; gap: 8px 16px; padding: 8px 16px; flex-wrap: wrap; }
.card-header-title { display: flex; flex: 1 1 160px; align-items: baseline; gap: 6px; min-width: 0; }
.card-header-title h2 { min-width: 0; margin: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 1rem; line-height: 1.5; font-weight: 600; }
.card-header-title :deep(> :not(h2)) { flex-shrink: 0; }
.card-header-actions { display: flex; align-items: center; gap: 8px; max-width: 100%; }
</style>
