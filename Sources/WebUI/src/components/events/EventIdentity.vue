<template>
  <div class="event-identity">
    <EventAvatar :name="name" :sources="avatarSources" />
    <div class="identity-details">
      <div class="identity-name-line"><v-icon v-if="appearance" :icon="appearance.icon" :color="appearance.color" size="13" :title="t(appearance.label)" /><span class="identity-name" :title="name">{{ name }}</span></div>
      <EventBadges :event="event" />
    </div>
    <button class="event-time mono" type="button" :aria-label="`${t('components.player.position')} ${formatDuration(event.offsetMs, true)}`" @click="$emit('seek', event.offsetMs)">{{ formatDuration(event.offsetMs) }}</button>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { RecordedEvent } from '@/models/media'
import { formatDuration } from '@/utilities/format'
import { eventAvatarSources } from '@/utilities/eventAssets'
import EventAvatar from '@/components/events/EventAvatar.vue'
import EventBadges from '@/components/events/EventBadges.vue'

const props = defineProps<{ sessionId: string; event: RecordedEvent }>()
defineEmits<{ seek: [offsetMs: number] }>()
const { t } = useI18n()
const name = computed(() => props.event.userName.trim() || t('components.events.guest'))
const avatarSources = computed(() => eventAvatarSources(props.sessionId, props.event))
const appearances: Record<string, { icon: string; color: string; label: string }> = {
  SuperChat: { icon: 'mdi-message-badge-outline', color: 'info', label: 'components.events.superChat' },
  Gift: { icon: 'mdi-gift-outline', color: 'primary', label: 'components.events.gift' },
  Guard: { icon: 'mdi-shield-crown-outline', color: 'warning', label: 'components.events.guard' },
  RedPocket: { icon: 'mdi-red-envelope', color: 'error', label: 'components.events.redPocket' },
}
const appearance = computed(() => appearances[props.event.kind])
</script>

<style scoped>
.event-identity { display: grid; grid-template-columns: 28px minmax(0, 1fr) auto; gap: 8px; align-items: center; min-width: 0; }
.identity-details { min-width: 0; }
.identity-name-line { display: flex; align-items: center; gap: 5px; min-width: 0; }
.identity-name { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; font-size: .75rem; font-weight: 550; color: rgba(var(--v-theme-on-surface), .72); }
.event-time { align-self: start; margin-top: 3px; padding: 2px 0 2px 4px; background: transparent; border: 0; border-radius: 3px; font-size: .62rem; line-height: 1.4; white-space: nowrap; color: rgba(var(--v-theme-on-surface), .4); cursor: pointer; }
.event-time:hover { color: rgb(var(--v-theme-primary)); }
.event-time:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 3px; }
</style>
