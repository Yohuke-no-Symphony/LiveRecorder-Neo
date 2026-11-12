<template>
  <div class="auth-page">
    <v-card class="setup-card">
      <div class="auth-brand"><span class="brand-mark">Y</span><strong>YOHUKE 录播库</strong></div>
      <h1 class="auth-title">初次安装</h1>
      <div class="d-flex ga-3 mb-8"><v-chip v-for="(name, index) in ['管理员', '存储', '数据库']" :key="name" :color="step === index + 1 ? 'primary' : undefined">{{ index + 1 }} · {{ name }}</v-chip></div>
      <form class="form-stack" @submit.prevent="next">
        <template v-if="step === 1">
          <v-text-field v-if="auth.requiresToken" v-model="form.token" label="安装令牌" type="password" required autocomplete="off" />
          <v-text-field v-model="form.userName" label="管理员名称" required autocomplete="nickname" />
          <v-text-field v-model="form.email" label="管理员邮箱" type="email" required autocomplete="email" />
          <v-text-field v-model="form.password" label="密码" type="password" required minlength="12" maxlength="256" autocomplete="new-password" />
          <v-text-field v-model="confirmation" label="确认密码" type="password" required autocomplete="new-password" :error-messages="confirmation && confirmation !== form.password ? '两次输入的密码不一致' : ''" />
        </template>
        <template v-else-if="step === 2">
          <v-text-field v-model="form.storagePath" label="录播目录" required placeholder="/srv/live-recorder" />
          <v-text-field v-model="form.ffmpegPath" label="FFmpeg" required />
          <v-text-field v-model="form.ffprobePath" label="FFprobe" required />
        </template>
        <template v-else>
          <v-text-field v-model="form.mongoConnectionString" label="MongoDB 连接地址" required autocomplete="off" />
          <v-text-field v-model="form.databaseName" label="数据库名称" required />
        </template>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <div class="d-flex justify-space-between ga-3 mt-3">
          <v-btn v-if="step > 1" variant="text" :disabled="busy" @click="step--">上一步</v-btn><v-spacer />
          <v-btn type="submit" color="primary" :loading="busy">{{ step === 3 ? '完成安装' : '下一步' }}<v-icon v-if="step < 3" icon="mdi-arrow-right" end /></v-btn>
        </div>
      </form>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'

const auth = useAuthStore()
const step = ref(1)
const confirmation = ref('')
const { busy, error, run } = useAction()
const form = reactive({ token: '', userName: '', email: '', password: '', storagePath: '', mongoConnectionString: 'mongodb://127.0.0.1:27017', databaseName: 'yohuke-live-recorder', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe' })

async function next(): Promise<void>
{
  if (step.value === 1 && confirmation.value !== form.password)
  {
    error.value = '两次输入的密码不一致'
    return
  }
  if (step.value < 3)
  {
    error.value = ''
    step.value++
    return
  }
  await run(async () =>
  {
    await AuthAPI.setup(form)
    window.location.assign('/login')
  })
}
</script>

<style scoped>.setup-card { width: min(100%, 590px); padding: 36px; }</style>
