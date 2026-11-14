<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <AppBrand />
      <h1 class="auth-title">{{ t('passwordReset.forgot') }}</h1>
      <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-6" />
      <v-alert v-if="loadError" type="error" class="mb-6">{{ loadError }}<v-btn variant="text" @click="loadOptions">{{ t('views.common.retry') }}</v-btn></v-alert>
      <v-alert v-else-if="enabled === false" type="info" class="mb-6">{{ t('passwordReset.unavailable') }}</v-alert>
      <v-alert v-else-if="sent" type="success" class="mb-6">{{ t('passwordReset.sent') }}</v-alert>
      <form v-else-if="enabled" class="form-stack" @submit.prevent="submit">
        <p class="text-body-2 muted">{{ t('passwordReset.requestDescription') }}</p>
        <v-text-field v-model="email" :label="t('views.common.email')" type="email" required autocomplete="email" :disabled="busy" />
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block :loading="busy">{{ t('passwordReset.send') }}</v-btn>
      </form>
      <div class="text-center mt-6"><router-link to="/login" class="text-primary text-body-2">{{ t('views.common.backToLogin') }}</router-link></div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'
import AppBrand from '@/components/common/AppBrand.vue'

const { t } = useI18n()
const email = ref('')
const enabled = ref<boolean>()
const sent = ref(false)
const { busy, error, run } = useAction()
const { busy: loading, error: loadError, run: runLoad } = useAction()

async function loadOptions(): Promise<void>
{
  if (loading.value) { return }
  await runLoad(async () => { enabled.value = (await AuthAPI.passwordResetOptions()).enabled })
}

async function submit(): Promise<void>
{
  if (busy.value || !enabled.value || sent.value) { return }
  await run(async () =>
  {
    await AuthAPI.requestPasswordReset(email.value.trim())
    sent.value = true
  })
}

onMounted(loadOptions)
</script>
