<template>
  <div class="danmu-content message-text"><span v-if="replyName" class="reply-name">{{ t('components.events.reply', { name: replyName }) }}</span><InlineEmoticons :content="content" :emoticons="event.payload?.emoticons" :session-id="sessionId" :sequence="event.sequence" :sticker="event.payload?.isEmoticon === true" /></div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { textValue } from '@/utilities/eventPresentation'
import InlineEmoticons from '@/components/events/InlineEmoticons.vue'

const { t } = useI18n()
const props = defineProps<{ event: RecordedEvent; sessionId?: string }>()
const replyName = computed(() => textValue(props.event.payload?.replyUserName))
const content = computed(() => textValue(props.event.payload?.content) || props.event.text)
</script>

<style scoped>
.message-text { white-space: pre-wrap; overflow-wrap: anywhere; }
.reply-name { color: rgb(var(--v-theme-secondary)); }
</style>
