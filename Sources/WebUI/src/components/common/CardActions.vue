<template>
  <div class="card-actions" :class="{ 'card-actions-embedded': embedded }">
    <slot/>
  </div>
</template>

<script setup lang="ts">
defineProps<{ embedded?: boolean }>()
</script>

<style scoped>
.card-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  flex-shrink: 0;
  gap: 8px;
  padding: 0 var(--ui-card-padding) var(--ui-card-padding);
}

.card-actions-embedded {
  padding: 0;
}
</style>
