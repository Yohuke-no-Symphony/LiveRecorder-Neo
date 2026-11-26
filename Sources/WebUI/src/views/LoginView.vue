<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <AppBrand/>
      <h1 class="auth-title">{{ t('views.login.title') }}</h1>
      <v-alert v-if="route.query.passwordReset === 'success'" type="success" class="mb-6">{{ t('passwordReset.success')
        }}</v-alert>
      <v-alert v-if="route.query.accountChanged === 'password' || route.query.accountChanged === 'email'"
               type="success" class="mb-6">
        {{ t(route.query.accountChanged === 'password' ? 'shell.account.passwordChanged' : 'shell.account.emailChanged') }}
      </v-alert>
      <v-alert v-if="route.query.registered" type="success" class="mb-6">{{ t(route.query.registered === 'active' ?
        'views.login.approved' : 'views.login.registered') }}</v-alert>
      <form class="form-stack" @submit.prevent="login">
        <v-text-field v-model="email" :label="t('views.common.email')" type="email" required autocomplete="email"/>
        <v-text-field v-model="password" :label="t('views.common.password')" :type="showPassword ? 'text' : 'password'"
                      required autocomplete="current-password"
                      :append-inner-icon="showPassword ? 'mdi-eye-off-outline' : 'mdi-eye-outline'"
                      @click:append-inner="showPassword = !showPassword"/>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" size="large" block :loading="busy">{{ t('views.common.login') }}</v-btn>
      </form>
      <div class="d-flex align-center justify-space-between ga-4 mt-6">
        <router-link to="/register" class="d-inline-flex align-center text-primary text-body-2">{{
          t('views.login.register') }}<v-icon icon="mdi-arrow-right" size="16" class="ml-1"/>
        </router-link>
        <router-link to="/forgot-password" class="text-primary text-body-2">{{ t('passwordReset.forgot') }}</router-link>
      </div>
      <template v-if="auth.providers.length">
        <v-divider class="my-6"/>
        <v-btn v-for="provider in auth.providers" :key="provider.id" variant="outlined" block class="mb-3"
               :loading="oauthBusy === provider.id" prepend-icon="mdi-account-circle-outline"
               @click="beginOAuth(provider.id)">{{ t('views.login.providerLogin', { provider: provider.name }) }}</v-btn>
      </template>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import AppBrand from '@/components/common/AppBrand.vue'
import {ref} from 'vue'
import {useRoute, useRouter} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAuthStore} from '@/stores/auth'
import {useAction} from '@/composables/useAction'
import {safeReturnPath} from '@/utilities/format'

const {t} = useI18n()

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const email = ref('')
const password = ref('')
const showPassword = ref(false)
const oauthBusy = ref('')
const {busy, error, run} = useAction()

async function login(): Promise<void> {
  await run(async () => {
    await auth.login(email.value, password.value)
    await router.replace(auth.hasAccess ? safeReturnPath(route.query.returnTo) : '/access')
  })
}

async function beginOAuth(id: string): Promise<void> {
  oauthBusy.value = id
  await run(async () => {
    sessionStorage.setItem('oauth_return_to', safeReturnPath(route.query.returnTo))
    const response = await AuthAPI.oauthBegin(id)
    window.location.assign(response.authorizationUrl)
  })
  oauthBusy.value = ''
}
</script>
