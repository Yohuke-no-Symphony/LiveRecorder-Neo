<template>
  <v-navigation-drawer v-model="drawer" :permanent="!mobile" width="240" color="background">
    <router-link to="/" class="shell-brand"><AppBrand /></router-link>
    <v-list class="px-3" nav density="comfortable">
      <v-list-item v-for="link in links" :key="link.to" :to="link.to" :prepend-icon="link.icon" :title="link.title" color="primary" rounded="lg" class="mb-1" :exact="link.to === '/'" />
      <template v-if="auth.isAdmin">
        <div class="nav-group">{{ t('shell.nav.management') }}</div>
        <v-list-item v-for="link in adminLinks" :key="link.to" :to="link.to" :prepend-icon="link.icon" :title="link.title" color="primary" rounded="lg" class="mb-1" />
      </template>
    </v-list>
    <template #append>
      <div class="shell-footer">
        <StorageBar v-if="recordingVolume" :volume="recordingVolume" />
      </div>
    </template>
  </v-navigation-drawer>
  <v-app-bar flat color="background" height="68" class="shell-topbar">
    <v-app-bar-nav-icon v-if="mobile" @click="drawer = !drawer" />
    <v-breadcrumbs :items="breadcrumbs" class="text-body-2 px-6" />
    <v-spacer />
    <v-btn :icon="theme.global.current.value.dark ? 'mdi-weather-sunny' : 'mdi-weather-night'" variant="text" :aria-label="t('shell.nav.toggleTheme')" @click="toggleTheme" />
    <v-btn icon="mdi-progress-clock" variant="text" to="/jobs" :aria-label="t('shell.nav.jobs')" />
    <v-menu><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" variant="text" class="mr-4" :aria-label="t('shell.nav.userMenu')"><UserAvatar :name="auth.user?.displayName ?? ''" :avatar-url="auth.user?.avatarUrl" size="30" class="mr-2" /><span v-if="!mobile">{{ auth.user?.displayName }}</span><v-icon icon="mdi-chevron-down" size="16" class="ml-2" /></v-btn></template><v-list density="compact"><v-list-item to="/account" :title="t('shell.nav.account')" prepend-icon="mdi-account-cog-outline" /><v-list-item :title="t('shell.nav.logout')" prepend-icon="mdi-logout" @click="logout" /></v-list></v-menu>
  </v-app-bar>
  <v-main><main class="page-container"><slot /></main></v-main>
  <v-snackbar v-model="errorOpen" color="error">{{ error }}</v-snackbar>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useDisplay, useTheme } from 'vuetify'
import { useAuthStore } from '@/stores/auth'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import type { StorageVolume } from '@/models/media'
import StorageBar from '@/components/common/StorageBar.vue'
import AppBrand from '@/components/common/AppBrand.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'

const { t } = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const theme = useTheme()
const { mobile } = useDisplay()
const drawer = ref(!mobile.value)
const volumes = ref<StorageVolume[]>([])
const recordingVolume = computed(() => volumes.value.find(volume => volume.id === 'recordings'))
const error = ref('')
const errorOpen = ref(false)
const links = computed(() => [
  { to: '/', title: t('shell.nav.library'), icon: 'mdi-play-box-multiple-outline' },
  { to: '/clips', title: t('shell.nav.clips'), icon: 'mdi-content-cut' },
  { to: '/jobs', title: t('shell.nav.jobs'), icon: 'mdi-progress-clock' },
  ...(auth.canViewAnalytics ? [{ to: '/analytics', title: t('shell.nav.analytics'), icon: 'mdi-chart-timeline-variant' }] : []),
  { to: '/trash', title: t('shell.nav.trash'), icon: 'mdi-delete-outline' },
])
const adminLinks = computed(() => [
  { to: '/admin/targets', title: t('shell.nav.targets'), icon: 'mdi-record-circle-outline' },
  { to: '/admin/accounts', title: t('shell.nav.accounts'), icon: 'mdi-account-box-outline' },
  { to: '/admin/users', title: t('shell.nav.users'), icon: 'mdi-account-group-outline' },
  { to: '/admin/storage', title: t('shell.nav.storage'), icon: 'mdi-harddisk' },
  { to: '/admin/settings', title: t('shell.nav.settings'), icon: 'mdi-cog-outline' },
])
const breadcrumbs = computed(() => [{ title: t('shell.nav.library'), to: '/' }, ...(route.path === '/' ? [] : [{ title: [...links.value, ...adminLinks.value].find(link => route.path.startsWith(link.to) && link.to !== '/')?.title ?? (route.path === '/account' ? t('shell.nav.account') : route.path.startsWith('/sessions') ? t('shell.nav.playRecording') : t('shell.nav.streamerRecordings')) }])])
usePolling(async () =>
{
  try
  {
    await auth.refresh()
    if (!auth.hasAccess)
    {
      await router.replace('/login')
      return
    }
    volumes.value = await MediaAPI.storage()
  }
  catch { volumes.value = [] }
}, 30000)

watch(() => auth.canViewAnalytics, allowed =>
{
  if (!allowed && route.path === '/analytics')
  {
    void router.replace('/')
  }
})

watch(() => auth.isAdmin, allowed =>
{
  if (!allowed && route.path.startsWith('/admin/'))
  {
    void router.replace('/')
  }
})

function toggleTheme(): void
{
  theme.change(theme.global.current.value.dark ? 'day' : 'night')
}

async function logout(): Promise<void>
{
  try
  {
    await auth.logout()
    await router.push('/login')
  }
  catch (value)
  {
    error.value = value instanceof Error ? value.message : t('shell.nav.logoutFailed')
    errorOpen.value = true
  }
}
</script>

<style scoped>
.shell-brand { display: flex; align-items: center; min-height: 90px; padding: 14px 20px; }
.nav-group { padding: 24px 16px 10px; color: rgba(var(--v-theme-on-surface), .32); font-size: .7rem; }
.shell-footer { padding: 24px; }
.shell-topbar { border-bottom: 1px solid rgba(var(--v-theme-on-surface), .06); }
</style>
