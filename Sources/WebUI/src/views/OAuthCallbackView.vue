<template>
  <div class="auth-page">
    <v-card class="auth-card text-center">
      <v-progress-circular v-if="busy" indeterminate color="primary" size="42" class="mb-6"/>
      <h1 class="auth-title">{{ error ? t('views.oauth.failed') : t('views.oauth.signingIn') }}</h1>
      <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
      <v-btn v-if="error" block color="primary" to="/login">{{ t('views.common.backToLogin') }}</v-btn>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import {onMounted} from 'vue'
import {useRoute, useRouter} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAuthStore} from '@/stores/auth'
import {useAction} from '@/composables/useAction'
import {safeReturnPath} from '@/utilities/format'

const {t} = useI18n()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const {busy, error, run} = useAction()
onMounted(() => {
  void run(async () => {
    const code = typeof route.query.code === 'string' ? route.query.code : ''
    const state = typeof route.query.state === 'string' ? route.query.state : ''
    if (route.query.error || !code || !state) {
      throw new Error(route.query.error === 'access_denied' ? t('views.oauth.cancelled') : t('views.oauth.invalidCallback'))
    }
    const response = await AuthAPI.oauthExchange(String(route.params.providerId), code, state)
    window.history.replaceState(null, '', route.path)
    if (response.registrationRequired) {
      auth.oauthDraft = {email: response.email, displayName: response.displayName}
      await router.replace('/register?oauth=1')
      return
    }
    await auth.refresh()
    const returnPath = safeReturnPath(sessionStorage.getItem('oauth_return_to'))
    sessionStorage.removeItem('oauth_return_to')
    await router.replace(auth.hasAccess ? returnPath : '/access')
  })
})
</script>
