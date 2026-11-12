<template>
  <div class="auth-page">
    <v-card class="auth-card text-center">
      <v-icon :icon="status.icon" :color="status.color" size="48" class="mb-6" />
      <h1 class="auth-title">{{ status.title }}</h1>
      <p v-if="auth.user?.email" class="muted mb-6">{{ auth.user.email }}</p>
      <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
      <v-btn color="primary" block :loading="busy" @click="refresh">{{ t('views.access.refresh') }}</v-btn>
      <v-btn variant="text" block class="mt-3" @click="logout">{{ t('views.common.logout') }}</v-btn>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'

const { t } = useI18n()

const auth = useAuthStore()
const router = useRouter()
const { busy, error, run } = useAction()
const status = computed(() => auth.user?.status === 'Rejected' ? { title: t('views.access.rejected'), icon: 'mdi-account-cancel-outline', color: 'error' } : auth.user?.status === 'Disabled' ? { title: t('views.access.disabled'), icon: 'mdi-account-lock-outline', color: 'error' } : { title: t('views.access.pending'), icon: 'mdi-account-clock-outline', color: 'warning' })

async function refresh(): Promise<void>
{
  await run(async () =>
  {
    await auth.refresh()
    if (auth.hasAccess)
    {
      await router.replace('/')
    }
    else if (!auth.user)
    {
      await router.replace('/login')
    }
  })
}
async function logout(): Promise<void>
{
  await run(async () => { await auth.logout(); await router.replace('/login') })
}
</script>
