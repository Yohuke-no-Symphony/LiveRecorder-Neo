<template>
  <v-dialog v-model="open" max-width="620" :persistent="saving" :aria-label="t('transcoding.transcodeTitle')">
    <ContentCard :title="t('transcoding.transcodeTitle')">
      <p class="text-body-2 mb-4">{{ t('transcoding.transcodeDescription', { count: sessionIds.length }) }}</p>
      <v-progress-linear v-if="loading" indeterminate class="mb-4" />
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
      <p v-if="!loading && !profiles.length" class="muted">{{ t('transcoding.noQualities') }}</p>
      <v-checkbox v-for="profile in profiles" :key="profile.id" v-model="selected" :value="profile.id" :label="`${profile.name} · ${profile.height}p`" :disabled="saving" hide-details />
      <v-checkbox v-model="force" :label="t('transcoding.replace')" :disabled="saving" class="mt-4" hide-details />
      <template #actions><v-btn variant="text" :disabled="saving" @click="open = false">{{ t('transcoding.cancel') }}</v-btn><v-btn color="primary" :disabled="loading || !selected.length || !sessionIds.length" :loading="saving" @click="create">{{ t('transcoding.create') }}</v-btn></template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import { normalizeTranscodingSettings, type TranscodingProfile } from '@/models/transcoding'
import ContentCard from '@/components/common/ContentCard.vue'

const open = defineModel<boolean>({ required: true })
const props = defineProps<{ sessionIds: string[] }>()
const emit = defineEmits<{ created: [queued: number] }>()
const { t } = useI18n()
const profiles = ref<TranscodingProfile[]>([])
const selected = ref<string[]>([])
const force = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let revision = 0
let disposed = false

watch(open, async value =>
{
  const request = ++revision
  if (!value) { return }
  loading.value = true
  error.value = ''
  selected.value = []
  profiles.value = []
  force.value = false
  try
  {
    const settings = normalizeTranscodingSettings((await MediaAPI.settings()).transcoding)
    if (disposed || request !== revision) { return }
    profiles.value = settings.qualityOrder.map(id => settings.profiles.find(profile => profile.id === id && profile.enabled)).filter((profile): profile is TranscodingProfile => !!profile)
    selected.value = profiles.value.map(profile => profile.id)
  }
  catch (value) { if (!disposed && request === revision) { error.value = value instanceof Error ? value.message : t('components.common.operationFailed') } }
  finally { if (!disposed && request === revision) { loading.value = false } }
}, { immediate: true })

async function create(): Promise<void>
{
  if (saving.value || loading.value || !selected.value.length || !props.sessionIds.length) { return }
  saving.value = true
  error.value = ''
  try
  {
    const result = await TranscodingAPI.createJobs([...props.sessionIds], [...selected.value], force.value)
    if (!disposed) { open.value = false; emit('created', result.queued) }
  }
  catch (value) { if (!disposed) { error.value = value instanceof Error ? value.message : t('components.common.operationFailed') } }
  finally { if (!disposed) { saving.value = false } }
}
onBeforeUnmount(() => { disposed = true; revision++ })
</script>
