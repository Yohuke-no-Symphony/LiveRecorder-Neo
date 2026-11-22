<template>
  <div class="event-message">
    <div v-if="event.kind === 'Gap'" class="gap-message">
      <v-icon icon="mdi-video-off-outline" size="15"/>
      <span>{{ event.text || t('components.events.gap') }}</span>
      <button class="gap-time mono" type="button" @click="$emit('seek', event.offsetMs)">{{
        formatDuration(event.offsetMs) }}
      </button>
    </div>
    <template v-else>
      <EventIdentity :session-id="sessionId" :event="event" @seek="$emit('seek', $event)"/>
      <div class="event-content">
        <DanmuContent v-if="event.kind === 'Danmu'" :event="event" :session-id="sessionId"/>
        <ScContent v-else-if="event.kind === 'SuperChat'" :event="event" :session-id="sessionId"/>
        <GiftContent v-else-if="event.kind === 'Gift'" :event="event"/>
        <GuardContent v-else-if="event.kind === 'Guard'" :event="event"/>
        <RedPocketContent v-else-if="event.kind === 'RedPocket'" :event="event"/>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import type {RecordedEvent} from '@/models/media'
import {formatDuration} from '@/utilities/format'
import EventIdentity from '@/components/events/EventIdentity.vue'
import DanmuContent from '@/components/events/DanmuContent.vue'
import ScContent from '@/components/events/ScContent.vue'
import GiftContent from '@/components/events/GiftContent.vue'
import GuardContent from '@/components/events/GuardContent.vue'
import RedPocketContent from '@/components/events/RedPocketContent.vue'

defineProps<{ sessionId: string; event: RecordedEvent }>()
defineEmits<{ seek: [offsetMs: number] }>()
const {t} = useI18n()
</script>

<style scoped>
.event-message {
  min-width: 0;
}

.event-content {
  min-width: 0;
  margin: 7px 0 0 36px;
  font-size: .8rem;
  line-height: 1.65;
  overflow-wrap: anywhere;
}

.gap-message {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: .72rem;
  color: rgba(var(--v-theme-on-surface), .45);
}

.gap-time {
  margin-left: auto;
  padding: 0;
  border: 0;
  background: transparent;
  white-space: nowrap;
  font-size: .62rem;
  color: inherit;
  cursor: pointer;
}

.gap-time:hover {
  color: rgb(var(--v-theme-primary));
}

.gap-time:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 3px;
}
</style>
