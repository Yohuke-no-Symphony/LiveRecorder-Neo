<template>
  <div v-if="isRoomAdmin || guard || medalName" class="message-badges">
    <span v-if="isRoomAdmin" class="identity-badge room-admin">{{ t('components.events.roomAdmin') }}</span>
    <span v-if="guard" class="identity-badge room-guard">{{ guard }}</span>
    <FanMedal v-if="medalName" :name="medalName" :level="medalLevel" />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { RecordedEvent } from '@/models/media'
import { guardLevelName, numberValue, textValue } from '@/utilities/eventPresentation'
import FanMedal from '@/components/events/FanMedal.vue'

const props = defineProps<{ event: RecordedEvent }>()
const { t } = useI18n()
const isRoomAdmin = computed(() => props.event.payload?.isRoomAdmin === true)
const guard = computed(() => guardLevelName(props.event.payload?.roomGuardLevel))
const medalName = computed(() => textValue(props.event.payload?.medalName))
const medalLevel = computed(() =>
{
  const value = numberValue(props.event.payload?.medalLevel)
  return value && value > 0 ? Math.floor(value) : undefined
})
</script>

<style scoped>
.message-badges { display: flex; flex: 0 1 auto; gap: 4px; flex-wrap: nowrap; align-items: center; min-width: 0; overflow: hidden; }
.identity-badge { flex: 0 0 auto; border-radius: 4px; padding: 0 4px; font-size: .6rem; line-height: 1.6; }
.room-admin { color: rgb(var(--v-theme-info)); background: rgba(var(--v-theme-info), .12); }
.room-guard { color: rgb(var(--v-theme-warning)); background: rgba(var(--v-theme-warning), .1); }
</style>
