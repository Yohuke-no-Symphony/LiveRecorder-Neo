<template>
  <span class="inline-emoticons"><template v-for="(token, index) in tokens" :key="index"><EventImage v-if="token.source"
                                                                                                     :sources="[token.source]"
                                                                                                     :alt="token.text"
                                                                                                     :width="dimension(token.width)"
                                                                                                     :height="dimension(token.height)"
                                                                                                     class="danmu-emoticon"
                                                                                                     :class="{ sticker }"><span>{{ token.text }}</span></EventImage><span
    v-else>{{ token.text }}</span></template></span>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {eventEmoticonUrl, tokenizeEventEmoticons} from '@/utilities/eventAssets'
import EventImage from '@/components/events/EventImage.vue'

const props = defineProps<{
  content: string;
  emoticons?: unknown;
  sessionId?: string;
  sequence?: number;
  sticker?: boolean
}>()
const tokens = computed(() => tokenizeEventEmoticons(props.content, props.emoticons).map(token => ({
  ...token,
  source: token.url ? eventEmoticonUrl(props.sessionId, props.sequence, token.emoticonIndex) : undefined
})))
const dimension = (value?: number) => value && Number.isFinite(value) && value > 0 ? Math.min(128, Math.round(value)) : undefined
</script>

<style scoped>
.inline-emoticons {
  display: inline;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.inline-emoticons :deep(.danmu-emoticon) {
  display: inline-block;
  width: auto;
  height: 1.25em;
  max-width: min(4em, 100%);
  object-fit: contain;
  vertical-align: middle;
  margin: 0 1px;
}

.inline-emoticons :deep(.danmu-emoticon.sticker) {
  height: auto;
  max-width: min(64px, 100%);
  max-height: 48px;
}
</style>
