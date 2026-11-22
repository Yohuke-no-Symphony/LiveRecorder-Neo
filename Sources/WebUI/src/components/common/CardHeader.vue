<template>
  <div class="card-header" :class="{ 'card-header-embedded': embedded, 'card-header-inline': inlineActions }">
    <div class="card-header-title">
      <h2 :id="titleId" :title="title">{{ title }}</h2>
      <slot name="suffix"/>
    </div>
    <div v-if="$slots.actions" class="card-header-actions">
      <slot name="actions"/>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{ title: string; titleId?: string; embedded?: boolean; inlineActions?: boolean }>()
</script>

<style scoped>
.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px 16px;
  padding: var(--ui-card-header-padding) var(--ui-card-padding);
  flex-wrap: wrap;
}

.card-header-title {
  display: flex;
  flex: 1 1 160px;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
}

.card-header-title h2 {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--ui-card-title-size);
  line-height: 1.5;
  font-weight: 600;
}

.card-header-title :deep(> :not(h2)) {
  flex-shrink: 0;
}

.card-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  max-width: 100%;
}

.card-header.card-header-embedded {
  padding: 0;
}

.card-header.card-header-inline {
  justify-content: flex-start;
}

.card-header-inline .card-header-title {
  flex: 0 1 auto;
}
</style>
