<template>
  <v-btn icon="mdi-cog-outline" variant="text" size="small" :aria-label="t('components.events.settings')" :title="t('components.events.settings')" :disabled="!auth.user" @click="openSettings" />
  <v-dialog v-model="open" max-width="400" :persistent="auth.playbackPreferenceSaving">
    <v-card :title="t('components.events.settings')">
      <v-form @submit.prevent="save">
        <v-card-text>
          <v-alert v-if="error" type="error" density="compact" class="mb-4">{{ error }}</v-alert>
          <v-text-field v-model.number="draft" type="number" min="1" :max="MAX_PLAYBACK_EVENT_RETENTION_COUNT" step="1" :label="t('components.events.retentionCount')" :hint="t('components.events.retentionHint')" persistent-hint :error-messages="valid ? [] : [t('components.events.retentionInvalid')]" :disabled="auth.playbackPreferenceSaving" autofocus />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="auth.playbackPreferenceSaving" @click="open = false">{{ t('components.common.cancel') }}</v-btn>
          <v-btn type="submit" color="primary" :disabled="!valid || !auth.user" :loading="auth.playbackPreferenceSaving">{{ t('components.common.confirm') }}</v-btn>
        </v-card-actions>
      </v-form>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import { DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT, MAX_PLAYBACK_EVENT_RETENTION_COUNT, isPlaybackEventRetentionCount } from '@/models/playbackPreferences'

const { t } = useI18n()
const auth = useAuthStore()
const open = ref(false)
const draft = ref<number | string>(DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT)
const valid = computed(() => isPlaybackEventRetentionCount(draft.value))
const error = ref('')
let dialogRevision = 0

function openSettings(): void
{
  dialogRevision++
  draft.value = auth.user?.playbackEventRetentionCount ?? DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT
  error.value = ''
  open.value = true
}

async function save(): Promise<void>
{
  if (!isPlaybackEventRetentionCount(draft.value) || !auth.user || auth.playbackPreferenceSaving)
  {
    return
  }
  const count = draft.value
  const userId = auth.user.id
  const operation = dialogRevision
  error.value = ''
  try
  {
    await auth.setPlaybackEventRetentionCount(count)
    if (operation === dialogRevision && userId === auth.user?.id)
    {
      open.value = false
    }
  }
  catch (value)
  {
    if (operation === dialogRevision && userId === auth.user?.id)
    {
      error.value = value instanceof Error ? value.message : t('components.common.operationFailed')
    }
  }
}

watch(() => auth.user?.id, () =>
{
  dialogRevision++
  open.value = false
  error.value = ''
})
</script>
