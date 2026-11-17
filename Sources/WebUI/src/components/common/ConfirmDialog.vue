<template>
  <v-dialog v-model="opened" max-width="440" :persistent="busy" :aria-label="title">
    <ContentCard :title="title">
      <div v-if="message || error" class="d-flex flex-column ga-4">
        <p v-if="message">{{ message }}</p>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
      </div>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
        <v-btn :color="danger ? 'error' : 'primary'" :loading="busy" @click="$emit('confirm')">{{ actionLabel ?? t('components.common.confirm') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'

const { t } = useI18n()

const opened = defineModel<boolean>({ required: true })
defineProps<{ title: string; message?: string; actionLabel?: string; danger?: boolean; busy?: boolean; error?: string }>()
defineEmits<{ confirm: [] }>()
</script>
