<template>
  <div v-if="open" class="playback-list-search" role="search" :aria-label="label">
    <v-text-field ref="input" v-model="searchText" :label="label" :aria-label="label" prepend-inner-icon="mdi-magnify" density="compact" hide-details @keydown.esc.stop.prevent="emit('close')">
      <template #append-inner>
        <v-btn type="button" icon="mdi-close" variant="text" size="x-small" :aria-label="t('components.player.closeSearch')" :title="t('components.player.closeSearch')" @click="emit('close')" />
      </template>
    </v-text-field>
  </div>
</template>

<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{ open: boolean; label: string }>()
const searchText = defineModel<string>({ required: true })
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const input = ref<{ focus: () => void }>()

watch(() => props.open, async open =>
{
  if (open)
  {
    await nextTick()
    input.value?.focus()
  }
}, { immediate: true })
</script>

<style scoped>
.playback-list-search { flex-shrink: 0; min-width: 0; padding: 0 var(--ui-card-padding) var(--ui-card-header-padding); }
</style>
