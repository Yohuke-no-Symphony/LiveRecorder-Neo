<template>
  <time v-if="!session.isPermanent && session.expiresAtUtc" class="session-expiry" :datetime="session.expiresAtUtc"
        :title="t('shell.retention.expiryDescription')">
    <v-icon icon="mdi-clock-outline" size="14"/>
    <span>{{ t('shell.retention.expiresAt', {date: formatDate(session.expiresAtUtc)}) }}</span>
  </time>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import type {BroadcastSession} from '@/models/media'
import {formatDate} from '@/utilities/format'

defineProps<{ session: Pick<BroadcastSession, 'isPermanent' | 'expiresAtUtc'> }>()
const {t} = useI18n()
</script>

<style scoped>
.session-expiry {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  color: rgba(var(--v-theme-on-surface), .65);
  font-size: .75rem;
  line-height: 1.5;
}

.session-expiry > span {
  min-width: 0;
}
</style>
