<template>
  <PageHeader :title="profile?.displayName ?? t('shell.nav.userProfile')">
    <template v-if="profile" #avatar>
      <UserAvatar :name="profile.displayName" :avatar-url="profile.avatarUrl" :size="58"/>
    </template>
    <v-btn v-if="profile && profile.id === auth.user?.id" variant="tonal" to="/account">
      {{ t('shell.nav.account') }}
    </v-btn>
  </PageHeader>
  <v-progress-linear v-if="busy && !profile" indeterminate color="primary"/>
  <v-alert v-else-if="error" type="error">
    {{ error }}
    <template #append>
      <v-btn variant="text" size="small" @click="loadProfile">{{ t('views.common.retry') }}</v-btn>
    </template>
  </v-alert>
  <ContentCard v-if="profile" :title="t('sharing.profileShares')">
    <p v-if="!auth.canViewRecordings" class="text-body-2 muted">{{ t('sharing.profilePermission') }}</p>
    <template v-else>
      <v-progress-linear v-if="loadingShares" indeterminate color="primary"/>
      <v-list v-if="shares.length" bg-color="transparent" lines="two">
        <v-list-item v-for="share in shares" :key="share.id" :title="share.title"
                     :to="{name: 'shared-clip', params: {id: share.id, token: share.token}}">
          <template #subtitle>
            <span class="mono">{{ formatDuration(share.durationMs, true) }}</span> · {{ formatBytes(share.bytes) }}
            · <time :datetime="share.createdAtUtc">{{ formatDate(share.createdAtUtc) }}</time>
          </template>
        </v-list-item>
      </v-list>
      <v-alert v-if="shareError" type="error">{{ shareError }}</v-alert>
      <EmptyState v-else-if="!loadingShares && !shares.length" :title="t('sharing.noProfileShares')" icon="mdi-share-variant-outline"/>
    </template>
    <template v-if="auth.canViewRecordings && (cursor || shareError)" #actions>
      <v-btn variant="tonal" color="primary" :loading="loadingShares" @click="loadShares">
        {{ t(shareError ? 'views.common.retry' : 'sharing.moreProfileShares') }}
      </v-btn>
    </template>
  </ContentCard>
</template>

<script setup lang="ts">
import {onMounted, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useRoute} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {MediaAPI} from '@/apis/MediaAPI'
import ContentCard from '@/components/common/ContentCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import {useAction} from '@/composables/useAction'
import type {PublicUserProfile} from '@/models/auth'
import type {PublicClipShare, PublicClipShareCursor} from '@/models/clipShare'
import {useAuthStore} from '@/stores/auth'
import {formatBytes, formatDate, formatDuration} from '@/utilities/format'

const {t} = useI18n()
const route = useRoute()
const auth = useAuthStore()
const userId = String(route.params.userId)
const profile = ref<PublicUserProfile>()
const shares = ref<PublicClipShare[]>([])
const cursor = ref<PublicClipShareCursor | null>(null)
const {busy, error, run} = useAction()
const {busy: loadingShares, error: shareError, run: runShares} = useAction()

onMounted(loadProfile)
watch(() => auth.canViewRecordings, canView => {
  if (canView && profile.value && !shares.value.length) {
    void loadShares()
  }
})

async function loadProfile(): Promise<void> {
  await run(async () => {
    profile.value = await AuthAPI.publicProfile(userId)
  })
  if (profile.value && auth.canViewRecordings) {
    await loadShares()
  }
}

async function loadShares(): Promise<void> {
  if (!loadingShares.value && auth.canViewRecordings) {
    await runShares(async () => {
      const result = await MediaAPI.publicClipShares(userId, cursor.value)
      shares.value.push(...result.items)
      cursor.value = result.nextCursor
    })
  }
}
</script>

<style scoped>
:deep(.page-title) {
  overflow-wrap: anywhere;
}
</style>
