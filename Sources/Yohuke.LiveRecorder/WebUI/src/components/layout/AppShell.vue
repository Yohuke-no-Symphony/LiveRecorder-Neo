<template>
  <v-navigation-drawer v-model="drawer" :permanent="!mobile" width="240" color="background">
    <router-link to="/" class="shell-brand"><span class="brand-mark">Y</span><div><strong>YOHUKE</strong><span>录播库</span></div></router-link>
    <v-list class="px-3" nav density="comfortable">
      <v-list-item v-for="link in links" :key="link.to" :to="link.to" :prepend-icon="link.icon" :title="link.title" color="primary" rounded="lg" class="mb-1" :exact="link.to === '/'" />
      <template v-if="auth.isAdmin">
        <div class="nav-group">管理</div>
        <v-list-item v-for="link in adminLinks" :key="link.to" :to="link.to" :prepend-icon="link.icon" :title="link.title" color="primary" rounded="lg" class="mb-1" />
      </template>
    </v-list>
    <template #append>
      <div class="shell-footer">
        <StorageBar v-if="volumes[0]" :volume="volumes[0]" />
      </div>
    </template>
  </v-navigation-drawer>
  <v-app-bar flat color="background" height="68" class="shell-topbar">
    <v-app-bar-nav-icon v-if="mobile" @click="drawer = !drawer" />
    <v-breadcrumbs :items="breadcrumbs" class="text-body-2 px-6" />
    <v-spacer />
    <v-btn :icon="theme.global.current.value.dark ? 'mdi-weather-sunny' : 'mdi-weather-night'" variant="text" aria-label="切换主题" @click="toggleTheme" />
    <v-btn icon="mdi-progress-clock" variant="text" to="/jobs" aria-label="后台任务" />
    <v-menu><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" variant="text" class="mr-4" aria-label="用户菜单"><v-avatar size="30" color="surface-variant" class="mr-2"><v-img v-if="auth.user?.avatarUrl" :src="auth.user.avatarUrl" /><span v-else>{{ auth.user?.displayName?.slice(0, 1) }}</span></v-avatar><span v-if="!mobile">{{ auth.user?.displayName }}</span><v-icon icon="mdi-chevron-down" size="16" class="ml-2" /></v-btn></template><v-list density="compact"><v-list-item to="/account" title="登录方式" prepend-icon="mdi-link-variant" /><v-list-item title="退出登录" prepend-icon="mdi-logout" @click="logout" /></v-list></v-menu>
  </v-app-bar>
  <v-main><main class="page-container"><slot /></main></v-main>
  <v-snackbar v-model="errorOpen" color="error">{{ error }}</v-snackbar>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useDisplay, useTheme } from 'vuetify'
import { useAuthStore } from '@/stores/auth'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import type { StorageVolume } from '@/models/media'
import StorageBar from '@/components/common/StorageBar.vue'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const theme = useTheme()
const { mobile } = useDisplay()
const drawer = ref(!mobile.value)
const volumes = ref<StorageVolume[]>([])
const error = ref('')
const errorOpen = ref(false)
const links = [
  { to: '/', title: '媒体库', icon: 'mdi-play-box-multiple-outline' },
  { to: '/clips', title: '我的切片', icon: 'mdi-content-cut' },
  { to: '/jobs', title: '后台任务', icon: 'mdi-progress-clock' },
  { to: '/analytics', title: '直播分析', icon: 'mdi-chart-timeline-variant' },
  { to: '/trash', title: '回收站', icon: 'mdi-delete-outline' },
]
const adminLinks = [
  { to: '/admin/targets', title: '录制管理', icon: 'mdi-record-circle-outline' },
  { to: '/admin/accounts', title: 'B站账号', icon: 'mdi-account-box-outline' },
  { to: '/admin/users', title: '用户与邀请', icon: 'mdi-account-group-outline' },
  { to: '/admin/settings', title: '设置', icon: 'mdi-cog-outline' },
]
const breadcrumbs = computed(() => [{ title: '媒体库', to: '/' }, ...(route.path === '/' ? [] : [{ title: [...links, ...adminLinks].find(link => route.path.startsWith(link.to) && link.to !== '/')?.title ?? (route.path === '/account' ? '登录方式' : route.path.startsWith('/sessions') ? '播放录播' : '主播录播') }])])
usePolling(async () =>
{
  try { volumes.value = await MediaAPI.storage() }
  catch { volumes.value = [] }
}, 30000)

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
    error.value = value instanceof Error ? value.message : '退出失败'
    errorOpen.value = true
  }
}
</script>

<style scoped>
.shell-brand { display: flex; align-items: center; gap: 12px; height: 90px; padding: 14px 27px; }
.shell-brand strong { display: block; font-size: .98rem; letter-spacing: .13em; }
.shell-brand span:last-child { display: block; color: rgba(var(--v-theme-on-surface), .42); font-size: .65rem; letter-spacing: .2em; margin-top: 2px; }
.nav-group { padding: 24px 16px 10px; color: rgba(var(--v-theme-on-surface), .32); font-size: .7rem; }
.shell-footer { padding: 24px; }
.shell-topbar { border-bottom: 1px solid rgba(var(--v-theme-on-surface), .06); }
</style>
