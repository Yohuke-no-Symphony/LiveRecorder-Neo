<template>
  <v-dialog v-model="opened" max-width="440">
    <v-card class="pa-6">
      <h2 class="text-h6 mb-4">{{ title }}</h2>
      <p v-if="message" class="mb-6">{{ message }}</p>
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
      <div class="d-flex justify-end ga-3">
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
        <v-btn :color="danger ? 'error' : 'primary'" :loading="busy" @click="$emit('confirm')">{{ actionLabel ?? t('components.common.confirm') }}</v-btn>
      </div>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const opened = defineModel<boolean>({ required: true })
defineProps<{ title: string; message?: string; actionLabel?: string; danger?: boolean; busy?: boolean; error?: string }>()
defineEmits<{ confirm: [] }>()
</script>
