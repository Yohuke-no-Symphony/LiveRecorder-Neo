<template>
  <div v-if="hasMore" class="d-flex justify-center mt-8"><v-btn variant="tonal" :loading="loading" prepend-icon="mdi-chevron-down" @click="$emit('load')">{{ t('components.common.loadMore') }}</v-btn></div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

defineProps<{ hasMore: boolean; loading: boolean }>()
defineEmits<{ load: [] }>()
</script>
