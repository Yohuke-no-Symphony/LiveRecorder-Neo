<template>
  <v-app>
    <div v-if="startupError" class="auth-page">
      <v-card class="auth-card">
        <h1 class="auth-title">{{ t('core.errors.startup') }}</h1>
        <v-alert type="error" class="mb-6">{{ startupError }}</v-alert>
        <v-btn block color="primary" @click="reload">{{ t('core.errors.retry') }}</v-btn>
      </v-card>
    </div>
    <AppShell v-else-if="showShell"><router-view :key="route.path" /></AppShell>
    <router-view v-else-if="auth.ready" />
    <div v-else class="auth-page"><v-progress-circular indeterminate color="primary" /></div>
    <div v-if="!showShell" class="public-language-menu">
      <AppLanguageMenu />
    </div>
  </v-app>
</template>

<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import router from './router'
import { useAuthStore } from '@/stores/auth'
import AppShell from '@/components/layout/AppShell.vue'
import AppLanguageMenu from '@/components/layout/AppLanguageMenu.vue'

const auth = useAuthStore()
const route = useRoute()
const { t, locale } = useI18n()
const startupError = ref('')
const showShell = computed(() => !startupError.value && auth.hasAccess && !route.meta.public)
watchEffect(() =>
{
  document.documentElement.lang = locale.value
  document.title = t('core.brand.name')
})
router.onError(error => { startupError.value = error.message })
void auth.bootstrap().catch(error => { startupError.value = error instanceof Error ? error.message : t('core.errors.connection') })

function reload(): void
{
  window.location.reload()
}
</script>

<style scoped>
.public-language-menu { position: fixed; inset-block-start: 12px; inset-inline-end: 16px; z-index: 900; }
</style>
