<template>
  <div class="empty-state">
    <v-icon :icon="icon" size="42" color="secondary"/>
    <p class="text-h6 mt-4 mb-5">{{ title }}</p>
    <slot/>
  </div>
</template>

<script setup lang="ts">
withDefaults(defineProps<{ title: string; icon?: string }>(), {icon: 'mdi-folder-play-outline'})
</script>
