<template>
  <header class="page-header">
    <div class="d-flex align-center ga-4">
      <slot name="avatar"/>
      <div>
        <div v-if="eyebrow" class="page-eyebrow">{{ eyebrow }}</div>
        <h1 class="page-title">{{ title }}</h1>
      </div>
    </div>
    <div class="page-actions">
      <slot/>
    </div>
  </header>
</template>

<script setup lang="ts">
defineProps<{ title: string; eyebrow?: string }>()
</script>

<style scoped>
.page-eyebrow {
  margin-bottom: 4px;
  line-height: 1.4;
}

.page-title {
  margin: 0;
}
</style>
