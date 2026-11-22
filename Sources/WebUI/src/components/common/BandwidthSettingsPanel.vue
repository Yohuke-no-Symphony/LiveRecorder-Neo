<template>
  <div class="settings-grid">
    <ContentCard :title="t('bandwidthSettings.userLimitTitle')">
      <div class="form-stack">
        <v-text-field :model-value="userLimitMiB" :label="t('bandwidthSettings.userLimit')"
                      :hint="t('bandwidthSettings.unlimited')" persistent-hint hide-details="auto"
                      :error-messages="userLimitValid ? '' : t('bandwidthSettings.invalidRate')" :disabled="disabled"
                      type="number" min="0" step="any" @update:model-value="updateUserLimit"/>
      </div>
    </ContentCard>
    <ContentCard :title="t('bandwidthSettings.smartTitle')">
      <div class="form-stack">
        <v-switch v-model="settings.smartLimitEnabled" :label="t('bandwidthSettings.smartEnabled')" :disabled="disabled"
                  hide-details/>
        <v-text-field :model-value="totalMbps" :label="t('bandwidthSettings.total')" :error-messages="totalError"
                      :disabled="disabled || !settings.smartLimitEnabled" hide-details="auto" type="number" min="0"
                      step="any" @update:model-value="updateTotal"/>
        <v-text-field v-model.number="settings.downloadGuaranteedPercent" :label="t('bandwidthSettings.guarantee')"
                      :error-messages="guaranteeValid ? '' : t('bandwidthSettings.invalidGuarantee')"
                      :disabled="disabled || !settings.smartLimitEnabled" hide-details="auto" type="number" min="0"
                      max="100" step="1"/>
      </div>
    </ContentCard>
  </div>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from './ContentCard.vue'
import {
  BYTES_PER_MIB,
  BYTES_PER_MEGABIT,
  bandwidthInputBytes,
  isValidBandwidthRate,
  isValidDownloadGuaranteedPercent,
  type BandwidthSettings
} from '@/models/bandwidth'

defineProps<{ disabled?: boolean }>()
const settings = defineModel<BandwidthSettings>({required: true})
const {t} = useI18n()
const userLimitMiB = ref<string | number | null>(0)
const totalMbps = ref<string | number | null>(0)
const userLimitValid = computed(() => isValidBandwidthRate(settings.value.userDownloadLimitBytesPerSecond))
const guaranteeValid = computed(() => isValidDownloadGuaranteedPercent(settings.value.downloadGuaranteedPercent))
const totalError = computed(() => {
  if (!isValidBandwidthRate(settings.value.totalBytesPerSecond)) {
    return t('bandwidthSettings.invalidRate')
  }
  return settings.value.smartLimitEnabled && settings.value.totalBytesPerSecond === 0 ? t('bandwidthSettings.invalidTotal') : ''
})

watch(settings, value => {
  userLimitMiB.value = value.userDownloadLimitBytesPerSecond / BYTES_PER_MIB
  totalMbps.value = value.totalBytesPerSecond / BYTES_PER_MEGABIT
}, {immediate: true})

function updateUserLimit(value: string | number | null): void {
  userLimitMiB.value = value
  settings.value.userDownloadLimitBytesPerSecond = bandwidthInputBytes(value, BYTES_PER_MIB)
}

function updateTotal(value: string | number | null): void {
  totalMbps.value = value
  settings.value.totalBytesPerSecond = bandwidthInputBytes(value, BYTES_PER_MEGABIT)
}
</script>
