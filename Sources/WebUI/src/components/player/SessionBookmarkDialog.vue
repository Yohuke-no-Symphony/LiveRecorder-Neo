<template>
  <v-dialog v-model="opened" max-width="500" :persistent="busy" :aria-label="bookmark ? t('components.bookmarks.edit') : t('components.bookmarks.create')">
    <ContentCard :title="bookmark ? t('components.bookmarks.edit') : t('components.bookmarks.create')">
      <form class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="title" :label="t('components.bookmarks.name')" :disabled="busy" maxlength="180" required autofocus />
        <v-text-field v-model="timeInput" :label="t('components.bookmarks.time')" :disabled="busy" class="mono" placeholder="00:00:00.000" required />
        <v-alert v-if="validationError || error" type="error">{{ validationError || error }}</v-alert>
        <CardActions embedded>
          <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
          <v-btn type="submit" color="primary" :loading="busy" :disabled="busy">{{ t('components.bookmarks.save') }}</v-btn>
        </CardActions>
      </form>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import ContentCard from '@/components/common/ContentCard.vue'
import CardActions from '@/components/common/CardActions.vue'
import { useI18n } from 'vue-i18n'
import type { SessionBookmark } from '@/models/sessionBookmark'
import { formatTimecode } from '@/utilities/format'
import { parseTimecode } from '@/utilities/timeline'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ bookmark?: SessionBookmark; offsetMs: number; durationMs: number; busy: boolean; error: string }>()
const emit = defineEmits<{ save: [value: { title: string; offsetMs: number }] }>()
const { t } = useI18n()
const title = ref('')
const timeInput = ref('')
const validationError = ref('')
let capturedOffsetMs = 0
let capturedTimeInput = ''

watch(opened, value =>
{
  if (value)
  {
    const offset = props.bookmark?.offsetMs ?? props.offsetMs
    title.value = props.bookmark?.title ?? t('components.bookmarks.defaultTitle', { time: formatTimecode(offset) })
    timeInput.value = formatTimecode(offset)
    capturedOffsetMs = offset
    capturedTimeInput = timeInput.value
    validationError.value = ''
  }
}, { immediate: true })

function submit(): void
{
  if (props.busy)
  {
    return
  }
  validationError.value = ''
  const name = title.value.trim()
  if (!name || name.length > 180)
  {
    validationError.value = t('components.bookmarks.invalidTitle')
    return
  }
  const offsetMs = timeInput.value === capturedTimeInput ? capturedOffsetMs : parseTimecode(timeInput.value)
  if (offsetMs === undefined || !Number.isFinite(offsetMs) || offsetMs < 0 || offsetMs > props.durationMs)
  {
    validationError.value = t('components.bookmarks.invalidTime')
    return
  }
  emit('save', { title: name, offsetMs })
}
</script>
