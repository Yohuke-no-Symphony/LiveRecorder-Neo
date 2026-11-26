<template>
  <PageHeader :title="t('shell.nav.sharedClip')"/>
  <v-progress-linear v-if="busy && !passwordRequired" indeterminate color="primary"/>
  <v-alert v-else-if="unavailable" type="warning">{{ t('sharing.unavailable') }}</v-alert>
  <ContentCard v-else-if="passwordRequired" class="shared-clip" :title="t('sharing.unlockTitle')">
    <form :id="unlockFormId" class="form-stack" @submit.prevent="unlock">
      <v-text-field v-model="password" :label="t('sharing.password')" type="password" required minlength="4"
                    maxlength="64" :disabled="busy"/>
      <v-alert v-if="error" type="error">{{ error }}</v-alert>
    </form>
    <template #actions>
      <v-btn type="submit" :form="unlockFormId" color="primary" :loading="busy">{{ t('sharing.unlock') }}</v-btn>
    </template>
  </ContentCard>
  <v-alert v-else-if="error" type="error">
    {{ error }}
    <template #append>
      <v-btn variant="text" size="small" @click="load()">{{ t('views.common.retry') }}</v-btn>
    </template>
  </v-alert>
  <ContentCard v-else-if="clip" class="shared-clip" :title="clip.title">
    <div class="shared-clip-details text-body-2 muted">
      <span class="mono">{{ formatDuration(clip.durationMs, true) }}</span>
      <span>{{ formatBytes(clip.bytes) }}</span>
      <time :datetime="clip.expiresAtUtc">{{ t('views.clips.expires', { date: formatDate(clip.expiresAtUtc) }) }}</time>
    </div>
    <div v-if="clip.owner" class="mt-4">
      <UserIdentityLink :user-id="clip.owner.id" :name="clip.owner.displayName" :avatar-url="clip.owner.avatarUrl" :size="28"/>
    </div>
    <p v-if="!auth.canDownloadRecordings" class="text-body-2 mt-4">{{ t('sharing.downloadPermission') }}</p>
    <template v-if="auth.canDownloadRecordings" #actions>
      <v-btn :href="MediaAPI.downloadSharedClip(id, token)" download color="primary" prepend-icon="mdi-download">{{
        t('views.common.download') }}
      </v-btn>
    </template>
  </ContentCard>
</template>

<script setup lang="ts">
import {onMounted, ref, useId} from 'vue'
import {useI18n} from 'vue-i18n'
import {useRoute, useRouter} from 'vue-router'
import {ApiError} from '@/apis/APIBase'
import {MediaAPI} from '@/apis/MediaAPI'
import ContentCard from '@/components/common/ContentCard.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import UserIdentityLink from '@/components/common/UserIdentityLink.vue'
import {useAction} from '@/composables/useAction'
import type {SharedClip} from '@/models/clipShare'
import {useAuthStore} from '@/stores/auth'
import {formatBytes, formatDate, formatDuration} from '@/utilities/format'
import {keepSharePassword, takeSharePassword} from '@/utilities/sharePassword'

const {t} = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const id = String(route.params.id)
const token = String(route.params.token)
const returnTo = route.fullPath
const clip = ref<SharedClip>()
const unavailable = ref(false)
const passwordRequired = ref(false)
const password = ref('')
const unlockFormId = useId()
const {busy, error, run} = useAction()

onMounted(() => load(takeSharePassword(route.path)))

async function load(candidate?: string): Promise<void> {
  await run(async () => {
    try {
      if (candidate) {
        await MediaAPI.unlockSharedClip(id, token, candidate)
      }
      clip.value = await MediaAPI.sharedClip(id, token)
      passwordRequired.value = false
      password.value = ''
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 401 && route.fullPath === returnTo) {
        if (candidate) {
          keepSharePassword(route.path, candidate)
        }
        await router.replace({path: '/login', query: {returnTo}})
        return
      }
      if (cause instanceof ApiError && (cause.status === 404 || cause.status === 410)) {
        unavailable.value = true
        password.value = ''
        return
      }
      if (cause instanceof ApiError && cause.code === 'LIBRARY_CLIP_SHARE_PASSWORD_REQUIRED') {
        passwordRequired.value = true
        return
      }
      if (cause instanceof ApiError && cause.code === 'LIBRARY_CLIP_SHARE_PASSWORD_INVALID') {
        passwordRequired.value = true
      }
      throw cause
    }
  })
}

async function unlock(): Promise<void> {
  if (!busy.value && password.value) {
    await load(password.value)
  }
}
</script>

<style scoped>
.shared-clip {
  max-width: 560px;
  margin-inline: auto;
}

.shared-clip-details {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
}
</style>
