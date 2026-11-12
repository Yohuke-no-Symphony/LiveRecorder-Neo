<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <div class="auth-brand"><span class="brand-mark">Y</span><strong>YOHUKE 录播库</strong></div>
      <h1 class="auth-title">登录</h1>
      <v-alert v-if="route.query.registered" type="success" class="mb-6">注册成功，等待管理员审核</v-alert>
      <form class="form-stack" @submit.prevent="login">
        <v-text-field v-model="email" label="邮箱" type="email" required autocomplete="email" />
        <v-text-field v-model="password" label="密码" :type="showPassword ? 'text' : 'password'" required autocomplete="current-password" :append-inner-icon="showPassword ? 'mdi-eye-off-outline' : 'mdi-eye-outline'" @click:append-inner="showPassword = !showPassword" />
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" size="large" block :loading="busy">登录</v-btn>
      </form>
      <template v-if="auth.providers.length">
        <v-divider class="my-6" />
        <v-btn v-for="provider in auth.providers" :key="provider.id" variant="outlined" block class="mb-3" :loading="oauthBusy === provider.id" prepend-icon="mdi-account-circle-outline" @click="beginOAuth(provider.id)">{{ provider.name }} 登录</v-btn>
      </template>
      <div class="text-center mt-6"><router-link to="/register" class="text-primary text-body-2">注册账号<v-icon icon="mdi-arrow-right" size="16" class="ml-1" /></router-link></div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { safeReturnPath } from '@/utilities/format'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const email = ref('')
const password = ref('')
const showPassword = ref(false)
const oauthBusy = ref('')
const { busy, error, run } = useAction()

async function login(): Promise<void>
{
  await run(async () =>
  {
    await auth.login(email.value, password.value)
    await router.replace(auth.hasAccess ? safeReturnPath(route.query.returnTo) : '/access')
  })
}

async function beginOAuth(id: string): Promise<void>
{
  oauthBusy.value = id
  await run(async () =>
  {
    sessionStorage.setItem('oauth_return_to', safeReturnPath(route.query.returnTo))
    const response = await AuthAPI.oauthBegin(id)
    window.location.assign(response.authorizationUrl)
  })
  oauthBusy.value = ''
}
</script>
