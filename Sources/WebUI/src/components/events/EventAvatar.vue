<template>
  <span class="event-avatar" aria-hidden="true">
    <EventImage :sources="sources"><span class="avatar-initial">{{ initial }}</span></EventImage>
  </span>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import EventImage from '@/components/events/EventImage.vue'

const props = defineProps<{ name: string; sources: readonly string[] }>()
const initial = computed(() => Array.from(props.name.trim())[0] || '·')
</script>

<style scoped>
.event-avatar { width: var(--event-avatar-size, 28px); height: var(--event-avatar-size, 28px); flex: 0 0 var(--event-avatar-size, 28px); border-radius: 50%; overflow: hidden; display: inline-flex; align-items: center; justify-content: center; color: rgba(var(--v-theme-on-surface), .65); background: rgba(var(--v-theme-on-surface), .075); }
.event-avatar :deep(img) { display: block; width: 100%; height: 100%; object-fit: cover; }
.avatar-initial { font-size: .8rem; line-height: 1; }
</style>
