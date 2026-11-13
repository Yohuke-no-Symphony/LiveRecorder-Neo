<template>
  <ul class="room-search-results">
    <li v-for="room in results" :key="room.roomId" class="room-search-result">
      <div class="room-search-identity">
        <div class="d-flex align-center flex-wrap ga-2">
          <span class="font-weight-medium">{{ room.ownerName || t('components.library.room', { roomId: room.roomId }) }}</span>
          <StatusChip :status="room.isLive ? 'live' : 'offline'" />
        </div>
        <div v-if="room.title" class="text-body-2 mt-2 room-search-title">{{ room.title }}</div>
        <div class="text-caption muted mt-2 room-search-details">
          <span>{{ t('components.library.room', { roomId: room.roomId }) }}</span>
          <span v-if="room.shortRoomId && room.shortRoomId !== room.roomId">{{ t('components.targets.shortRoom', { roomId: room.shortRoomId }) }}</span>
          <span>UID {{ room.ownerUid }}</span>
        </div>
        <div class="d-flex flex-wrap ga-2 mt-2">
          <v-chip v-for="source in room.sources" :key="source" size="x-small" variant="tonal">{{ t(`components.targets.sources.${source}`) }}</v-chip>
        </div>
      </div>
      <v-btn color="primary" size="small" :disabled="busy || monitoredRoomIds.includes(room.roomId)"
             :loading="busy && savingRoomId === room.roomId" :aria-label="t('components.targets.addRoom', { name: room.ownerName || room.roomId })"
             @click="emit('add', room)">
        {{ t(monitoredRoomIds.includes(room.roomId) ? 'components.targets.added' : 'components.targets.add') }}
      </v-btn>
    </li>
  </ul>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { BilibiliRoomSearchResult } from '@/models/bilibili'
import StatusChip from '@/components/common/StatusChip.vue'

const { t } = useI18n()
defineProps<{ results: BilibiliRoomSearchResult[]; monitoredRoomIds: readonly string[]; busy: boolean; savingRoomId: string | null }>()
const emit = defineEmits<{ add: [room: BilibiliRoomSearchResult] }>()
</script>

<style scoped>
.room-search-results { list-style: none; padding: 0; margin: 16px 0 0; display: grid; gap: 12px; }
.room-search-result { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 16px; padding: 16px; border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity)); border-radius: 12px; }
.room-search-identity { min-width: 0; }
.room-search-title { overflow-wrap: anywhere; }
.room-search-details { display: flex; flex-wrap: wrap; gap: 4px 16px; overflow-wrap: anywhere; }
@media (max-width: 480px) {
  .room-search-result { grid-template-columns: minmax(0, 1fr); gap: 12px; }
  .room-search-result > .v-btn { justify-self: end; }
}
</style>
