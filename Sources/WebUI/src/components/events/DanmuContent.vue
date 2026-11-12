<template>
  <div class="danmu-content">
    <div v-if="payload.isRoomAdmin === true || guard || medalName" class="message-badges"><span v-if="payload.isRoomAdmin === true" class="room-admin">{{ t('components.events.roomAdmin') }}</span><span v-if="guard" class="room-guard">{{ guard }}</span><span v-if="medalName" class="medal">{{ medalName }}<b v-if="medalLevel"> {{ formatNumber(medalLevel) }}</b></span></div>
    <div class="message-text"><span v-if="replyName" class="reply-name">{{ t('components.events.reply', { name: replyName }) }}</span><template v-for="(token, index) in tokens" :key="index"><img v-if="token.url" :src="token.url" :alt="token.text" :width="dimension(token.width)" :height="dimension(token.height)" class="danmu-emoticon" :class="{ sticker: payload.isEmoticon === true }" loading="lazy" /><span v-else>{{ token.text }}</span></template></div>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { formatNumber } from '@/utilities/format'
import { guardLevelName, numberValue, objectValue, textValue, tokenizeDanmu } from '@/utilities/eventPresentation'

const { t } = useI18n()
const props = defineProps<{ event: RecordedEvent }>()
const payload = computed(() => objectValue(props.event.payload))
const guard = computed(() => guardLevelName(payload.value.roomGuardLevel))
const medalName = computed(() => textValue(payload.value.medalName))
const medalLevel = computed(() => numberValue(payload.value.medalLevel))
const replyName = computed(() => textValue(payload.value.replyUserName))
const tokens = computed(() => tokenizeDanmu(textValue(payload.value.content) || props.event.text, payload.value.emoticons))
const dimension = (value?: number) => value && value > 0 ? Math.min(128, Math.round(value)) : undefined
</script>

<style scoped>.message-text { white-space: pre-wrap; overflow-wrap: anywhere; }.message-badges { display: flex; gap: 5px; flex-wrap: wrap; margin-bottom: 5px; }.message-badges > span { border-radius: 4px; padding: 0 5px; font-size: .6rem; line-height: 1.6; }.room-admin { color: rgb(var(--v-theme-info)); background: rgba(var(--v-theme-info), .12); }.room-guard { color: rgb(var(--v-theme-warning)); background: rgba(var(--v-theme-warning), .1); }.medal { color: rgb(var(--v-theme-secondary)); background: rgba(var(--v-theme-secondary), .1); }.reply-name { color: rgb(var(--v-theme-secondary)); }.danmu-emoticon { height: 1.7em; max-width: 5em; object-fit: contain; vertical-align: middle; margin: 0 2px; }.danmu-emoticon.sticker { height: auto; max-width: 130px; max-height: 96px; }</style>
