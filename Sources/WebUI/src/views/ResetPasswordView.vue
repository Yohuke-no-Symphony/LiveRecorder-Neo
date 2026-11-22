<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <AppBrand/>
      <h1 class="auth-title">{{ t('passwordReset.title') }}</h1>
      <template v-if="completed">
        <v-alert type="success" class="mb-6">{{ t('passwordReset.success') }}</v-alert>
        <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
        <v-btn color="primary" block :loading="busy" @click="finish">{{ t('views.common.backToLogin') }}</v-btn>
      </template>
      <v-alert v-else-if="!hasToken" type="error">{{ t('passwordReset.invalidLink') }}</v-alert>
      <form v-else class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="password" :label="t('passwordReset.newPassword')" type="password" required minlength="12"
                      maxlength="256" autocomplete="new-password" :disabled="busy"/>
        <v-text-field v-model="confirmation" :label="t('passwordReset.confirmPassword')" type="password" required
                      minlength="12" maxlength="256" autocomplete="new-password" :disabled="busy"/>
        <p class="text-body-2 muted">{{ t('passwordReset.expiredHint') }}</p>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block :loading="busy">{{ t('passwordReset.submit') }}</v-btn>
      </form>
      <div v-if="!completed" class="text-center mt-6">
        <router-link to="/forgot-password" class="text-primary text-body-2">{{ t('passwordReset.requestAgain') }}
        </router-link>
      </div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import {onBeforeUnmount, ref} from 'vue'
import {useRoute, useRouter} from 'vue-router'
import {useI18n} from 'vue-i18n'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'
import AppBrand from '@/components/common/AppBrand.vue'

const {t} = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
let token = new URLSearchParams(route.hash.slice(1)).get('token') ?? ''
const hasToken = ref(/^[A-Za-z0-9_-]{43}$/.test(token))
const password = ref('')
const confirmation = ref('')
const completed = ref(false)
const {busy, error, run} = useAction()
if (route.hash) {
  window.history.replaceState(window.history.state, '', `${window.location.pathname}${window.location.search}`)
  void router.replace({path: route.path, query: route.query, hash: ''})
}

async function returnToLogin(): Promise<void> {
  await auth.refresh()
  await router.replace({path: '/login', query: {passwordReset: 'success'}})
}

async function finish(): Promise<void> {
  if (!busy.value) {
    await run(returnToLogin)
  }
}

async function submit(): Promise<void> {
  if (busy.value || completed.value || !hasToken.value) {
    return
  }
  await run(async () => {
    if (password.value.length < 12 || password.value.length > 256) {
      throw new Error(t('passwordReset.passwordLength'))
    }
    if (password.value !== confirmation.value) {
      throw new Error(t('passwordReset.mismatch'))
    }
    await AuthAPI.completePasswordReset(token, password.value)
    completed.value = true
    token = ''
    password.value = ''
    confirmation.value = ''
    auth.clearSession()
    await returnToLogin()
  })
}

onBeforeUnmount(() => {
  token = ''
  password.value = ''
  confirmation.value = ''
})
</script>
