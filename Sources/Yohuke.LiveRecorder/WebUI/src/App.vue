<template>
  <v-app>
    <div v-if="startupError" class="auth-page">
      <v-card class="auth-card">
        <h1 class="auth-title">连接服务器失败</h1>
        <v-alert type="error" class="mb-6">{{ startupError }}</v-alert>
        <v-btn block color="primary" @click="reload">重试</v-btn>
      </v-card>
    </div>
    <AppShell v-else-if="auth.hasAccess && !route.meta.public"><router-view :key="route.path" /></AppShell>
    <router-view v-else-if="auth.ready" />
    <div v-else class="auth-page"><v-progress-circular indeterminate color="primary" /></div>
  </v-app>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import router from './router'
import { useAuthStore } from '@/stores/auth'
import AppShell from '@/components/layout/AppShell.vue'

const auth = useAuthStore()
const route = useRoute()
const startupError = ref('')
router.onError(error => { startupError.value = error.message })
void auth.bootstrap().catch(error => { startupError.value = error instanceof Error ? error.message : '无法连接服务器' })
function reload(): void
{
  window.location.reload()
}
</script>
