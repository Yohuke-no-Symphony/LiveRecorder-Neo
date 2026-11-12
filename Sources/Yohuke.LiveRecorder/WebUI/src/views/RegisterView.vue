<template>
  <div class="auth-page">
    <v-card class="auth-card">
      <div class="auth-brand"><span class="brand-mark">Y</span><strong>YOHUKE 录播库</strong></div>
      <h1 class="auth-title">{{ isOAuth ? '完成注册' : '注册' }}</h1>
      <form class="form-stack" @submit.prevent="register">
        <v-text-field v-model="form.displayName" label="显示名称" required maxlength="80" autocomplete="nickname" />
        <v-text-field v-model="form.email" label="邮箱" type="email" required autocomplete="email" />
        <EmailCodeField v-model="form.emailCode" :email="form.email" :disabled="busy" />
        <v-text-field v-if="!isOAuth" v-model="form.password" label="密码" type="password" required minlength="12" maxlength="256" autocomplete="new-password" />
        <v-text-field v-model="form.invitationCode" label="邀请码" required autocomplete="off" />
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block size="large" :loading="busy">提交注册</v-btn>
      </form>
      <div class="text-center mt-6"><router-link to="/login" class="text-primary text-body-2">返回登录</router-link></div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import EmailCodeField from '@/components/common/EmailCodeField.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const isOAuth = computed(() => route.query.oauth === '1')
const { busy, error, run } = useAction()
const form = reactive({ email: auth.oauthDraft?.email ?? '', displayName: auth.oauthDraft?.displayName ?? '', password: '', emailCode: '', invitationCode: '' })

async function register(): Promise<void>
{
  await run(async () =>
  {
    if (isOAuth.value)
    {
      const { password: _, ...registration } = form
      await AuthAPI.oauthRegister(registration)
      auth.oauthDraft = null
    }
    else
    {
      await AuthAPI.register(form)
    }
    await auth.refresh()
    await router.replace(auth.user ? '/access' : '/login?registered=1')
  })
}
</script>
