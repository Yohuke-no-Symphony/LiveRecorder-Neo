<template>
  <ContentCard :title="t('transcoding.qualities')">
    <template #header-actions>
      <v-btn prepend-icon="mdi-plus" variant="tonal" :disabled="disabled || settings.profiles.length >= 12"
             @click="addQuality">{{ t('transcoding.addQuality') }}
      </v-btn>
    </template>
    <v-switch v-model="settings.enabled" :label="t('transcoding.auto')" :disabled="disabled" color="primary"/>
    <p v-if="settings.profiles.length" class="text-body-2 muted mb-4">{{ t('transcoding.priorityHint') }}</p>
    <ReorderableList v-model="rows" :disabled="disabled"
                     :label="row => t('transcoding.reorder', { name: row.profile?.name || t('transcoding.original') })">
      <template #default="{ item }">
        <div class="quality-heading">
          <v-text-field v-if="item.profile" v-model="item.profile.name" :label="t('transcoding.name')"
                        :disabled="disabled" hide-details density="compact"/>
          <strong v-else class="original-label">{{ t('transcoding.original') }}</strong>
          <v-switch v-if="item.profile" :model-value="item.profile.enabled" :label="t('shell.settings.enabled')"
                    :disabled="disabled" hide-details density="compact" color="primary"
                    @update:model-value="setEnabled(item.id, !!$event)"/>
          <v-switch v-if="item.profile" v-model="item.profile.autoTranscode" :label="t('transcoding.autoTranscode')"
                    :disabled="disabled || !item.profile.enabled" hide-details density="compact" color="primary"/>
          <v-tooltip :text="t('transcoding.setDefault', { name: item.profile?.name || t('transcoding.original') })">
            <template #activator="{ props: tooltipProps }">
              <v-btn v-bind="tooltipProps"
                     :icon="settings.defaultPlaybackQualityId === item.id ? 'mdi-star' : 'mdi-star-outline'"
                     :color="settings.defaultPlaybackQualityId === item.id ? 'primary' : undefined" variant="text"
                     :disabled="disabled || item.profile?.enabled === false"
                     :aria-pressed="settings.defaultPlaybackQualityId === item.id"
                     :aria-label="t('transcoding.setDefault', { name: item.profile?.name || t('transcoding.original') })"
                     @click="settings.defaultPlaybackQualityId = item.id"/>
            </template>
          </v-tooltip>
          <v-btn v-if="item.profile" icon="mdi-delete-outline" variant="text" color="error" :disabled="disabled"
                 :aria-label="t('transcoding.removeQuality', { name: item.profile.name })"
                 @click="removeQuality(item.id)"/>
        </div>
        <div v-if="item.profile" class="quality-fields mt-4">
          <v-text-field v-model.number="item.profile.height" :label="t('transcoding.height')" type="number" min="64"
                        max="8640" step="2" :disabled="disabled"/>
          <v-text-field v-model.number="item.profile.videoBitrateKbps" :label="t('transcoding.videoBitrate')"
                        type="number" min="32" max="1000000" :disabled="disabled"/>
          <v-text-field :model-value="item.profile.maxFrameRate" :label="t('transcoding.frameRate')" type="number"
                        min="1" max="240" clearable :disabled="disabled"
                        @update:model-value="setFrameRate(item.id, $event)"/>
          <v-text-field v-model.number="item.profile.audioBitrateKbps" :label="t('transcoding.audioBitrate')"
                        type="number" min="16" max="512" :disabled="disabled"/>
          <v-text-field v-model.number="item.profile.segmentDurationSeconds" :label="t('transcoding.segmentDuration')"
                        type="number" min="0.1" max="300" step="any" :disabled="disabled"/>
          <v-text-field v-model.number="item.profile.keyframeIntervalSeconds" :label="t('transcoding.keyframeInterval')"
                        type="number" min="0.1" max="300" step="any" :disabled="disabled"/>
          <v-text-field v-model.number="item.profile.priority" :label="t('transcoding.priority')" type="number"
                        :min="MIN_TRANSCODE_PRIORITY" :max="MAX_TRANSCODE_PRIORITY" step="1" :disabled="disabled"
                        :error-messages="isValidTranscodePriority(item.profile.priority) ? undefined : t('transcoding.invalidPriority')"/>
        </div>
        <v-switch v-if="item.profile" v-model="item.profile.singleFile" :label="t('transcoding.singleFile')"
                  :disabled="disabled" hide-details color="primary"/>
        <p v-else class="text-body-2 muted mt-2">{{ t('transcoding.originalHint') }}</p>
      </template>
    </ReorderableList>
  </ContentCard>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import {
  MAX_TRANSCODE_PRIORITY,
  MIN_TRANSCODE_PRIORITY,
  ORIGINAL_QUALITY_ID,
  isValidTranscodePriority,
  type TranscodingSettings
} from '@/models/transcoding'
import ContentCard from './ContentCard.vue'
import ReorderableList from './ReorderableList.vue'

const settings = defineModel<TranscodingSettings>({required: true})
const props = defineProps<{ disabled?: boolean }>()
const {t} = useI18n()
const rows = computed({
  get: () => settings.value.qualityOrder.map(id => ({
    id,
    profile: settings.value.profiles.find(profile => profile.id === id)
  })),
  set: value => {
    settings.value.qualityOrder = value.map(row => row.id)
  },
})

function addQuality(): void {
  if (props.disabled || settings.value.profiles.length >= 12) {
    return
  }
  const id = crypto.randomUUID()
  settings.value.profiles.push({
    id,
    name: '1080p',
    height: 1080,
    videoBitrateKbps: 5000,
    maxFrameRate: null,
    audioBitrateKbps: 128,
    segmentDurationSeconds: 6,
    keyframeIntervalSeconds: 2,
    singleFile: false,
    enabled: true,
    autoTranscode: false,
    priority: 0,
  })
  settings.value.qualityOrder.push(id)
}

function removeQuality(id: string): void {
  if (props.disabled || id === ORIGINAL_QUALITY_ID) {
    return
  }
  settings.value.profiles = settings.value.profiles.filter(profile => profile.id !== id)
  settings.value.qualityOrder = settings.value.qualityOrder.filter(value => value !== id)
  if (settings.value.defaultPlaybackQualityId === id) {
    settings.value.defaultPlaybackQualityId = ORIGINAL_QUALITY_ID
  }
}

function setEnabled(id: string, enabled: boolean): void {
  if (props.disabled) {
    return
  }
  const profile = settings.value.profiles.find(profile => profile.id === id)
  if (!profile) {
    return
  }
  profile.enabled = enabled
  if (!enabled && settings.value.defaultPlaybackQualityId === id) {
    settings.value.defaultPlaybackQualityId = ORIGINAL_QUALITY_ID
  }
}

function setFrameRate(id: string, value: string | number | null): void {
  if (props.disabled) {
    return
  }
  const profile = settings.value.profiles.find(profile => profile.id === id)
  if (profile) {
    profile.maxFrameRate = value === null || value === '' ? null : Number(value)
  }
}
</script>

<style scoped>
.quality-heading {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

.quality-heading > .v-input:first-child {
  flex: 1;
  min-width: 160px;
}

.original-label {
  flex: 1;
  min-height: 40px;
  display: flex;
  align-items: center;
}

.quality-fields {
  display: grid;
  grid-template-columns: repeat(3, minmax(140px, 1fr));
  gap: 12px;
}

@media (max-width: 900px) {
  .quality-fields {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }
}

@media (max-width: 500px) {
  .quality-fields {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
