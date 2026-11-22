<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <AppBrand/>
      <h1 class="auth-title">{{ isOAuth ? t('views.register.complete') : t('views.register.title') }}</h1>
      <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-6"/>
      <v-alert v-if="settingsError" type="error" class="mb-6">
        {{ settingsError }}
        <v-btn variant="text" :loading="loading" @click="loadRegistrationOptions">{{ t('views.common.retry') }}</v-btn>
      </v-alert>
      <v-alert v-else-if="registrationOptions && !registrationOptions.enabled" type="info" class="mb-6">{{
        t('views.register.closed') }}
      </v-alert>
      <form v-if="registrationOptions?.enabled" class="form-stack" @submit.prevent="register">
        <v-text-field v-model="form.displayName" :label="t('views.register.displayName')" required maxlength="80"
                      autocomplete="nickname"/>
        <v-text-field v-model="form.email" :label="t('views.common.email')" type="email" required autocomplete="email"/>
        <EmailCodeField v-if="registrationOptions.emailVerificationRequired" v-model="form.emailCode"
                        :email="form.email" :disabled="busy"/>
        <v-text-field v-if="!isOAuth" v-model="form.password" :label="t('views.common.password')" type="password"
                      required minlength="12" maxlength="256" autocomplete="new-password"/>
        <v-text-field v-if="registrationOptions.invitationRequired" v-model="form.invitationCode"
                      :label="t('views.register.invitationCode')" required autocomplete="off"/>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block size="large" :loading="busy">{{ t('views.register.submit') }}</v-btn>
      </form>
      <div class="text-center mt-6">
        <router-link to="/login" class="text-primary text-body-2">{{ t('views.common.backToLogin') }}</router-link>
      </div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import AppBrand from '@/components/common/AppBrand.vue'
import {computed, onMounted, reactive, ref} from 'vue'
import {useRoute, useRouter} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAuthStore} from '@/stores/auth'
import {useAction} from '@/composables/useAction'
import EmailCodeField from '@/components/common/EmailCodeField.vue'
import type {RegistrationOptions} from '@/models/auth'

const {t} = useI18n()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const isOAuth = computed(() => route.query.oauth === '1')
const {busy, error, run} = useAction()
const {busy: loading, error: settingsError, run: runLoadSettings} = useAction()
const registrationOptions = ref<RegistrationOptions>()
const form = reactive({
  email: auth.oauthDraft?.email ?? '',
  displayName: auth.oauthDraft?.displayName ?? '',
  password: '',
  emailCode: '',
  invitationCode: ''
})

async function loadRegistrationOptions(): Promise<void> {
  await runLoadSettings(async () => {
    registrationOptions.value = await AuthAPI.registrationOptions()
  })
}

onMounted(loadRegistrationOptions)

async function register(): Promise<void> {
  const options = registrationOptions.value
  if (!options?.enabled) {
    return
  }
  await run(async () => {
    const registration = {
      email: form.email,
      displayName: form.displayName,
      emailCode: options.emailVerificationRequired ? form.emailCode : undefined,
      invitationCode: options.invitationRequired ? form.invitationCode : undefined,
    }
    const user = isOAuth.value
      ? await AuthAPI.oauthRegister(registration)
      : await AuthAPI.register({...registration, password: form.password})
    if (isOAuth.value) {
      auth.oauthDraft = null
    }
    await auth.refresh()
    await router.replace(user.status === 'Pending' ? '/login?registered=1' : '/login?registered=active')
  })
}
</script>
