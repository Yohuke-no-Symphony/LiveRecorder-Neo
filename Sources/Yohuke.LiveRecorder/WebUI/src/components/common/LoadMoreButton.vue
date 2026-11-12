<template>
  <div v-if="hasMore" class="d-flex justify-center mt-8"><v-btn variant="tonal" :loading="loading" prepend-icon="mdi-chevron-down" @click="$emit('load')">加载更多</v-btn></div>
</template>

<script setup lang="ts">
defineProps<{ hasMore: boolean; loading: boolean }>()
defineEmits<{ load: [] }>()
</script>
