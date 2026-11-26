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
  <section v-if="profile" :aria-labelledby="sharesHeadingId" class="profile-shares">
    <h2 :id="sharesHeadingId" class="section-heading mb-0">{{ t('sharing.profileShares') }}</h2>
    <ContentCard v-if="!auth.canViewRecordings">
      <p class="text-body-2 muted">{{ t('sharing.profilePermission') }}</p>
    </ContentCard>
    <template v-else>
      <v-progress-linear v-if="loadingShares" indeterminate color="primary"/>
      <ul v-if="shares.length" class="media-grid profile-share-grid" role="list">
        <li v-for="share in shares" :key="share.id">
          <VideoCard class="flex-grow-1" :title="share.title" :duration-ms="share.durationMs"
                     :cover-url="MediaAPI.sharedClipCover(share.id, share.token)" link-icon="mdi-arrow-right"
                     :to="{name: 'shared-clip', params: {id: share.id, token: share.token}}">
            <template #metadata>
              <span>{{ formatBytes(share.bytes) }}</span>
              <time :datetime="share.createdAtUtc">{{ formatDate(share.createdAtUtc) }}</time>
            </template>
          </VideoCard>
        </li>
      </ul>
      <v-alert v-if="shareError" type="error">{{ shareError }}</v-alert>
      <EmptyState v-else-if="!loadingShares && !shares.length" :title="t('sharing.noProfileShares')" icon="mdi-share-variant-outline"/>
      <CardActions v-if="cursor || shareError" embedded>
        <v-btn variant="tonal" color="primary" :loading="loadingShares" @click="loadShares">
          {{ t(shareError ? 'views.common.retry' : 'sharing.moreProfileShares') }}
        </v-btn>
      </CardActions>
    </template>
  </section>
</template>

<script setup lang="ts">
import {onMounted, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useRoute} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {MediaAPI} from '@/apis/MediaAPI'
import CardActions from '@/components/common/CardActions.vue'
import ContentCard from '@/components/common/ContentCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import VideoCard from '@/components/common/VideoCard.vue'
import {useAction} from '@/composables/useAction'
import type {PublicUserProfile} from '@/models/auth'
import type {PublicClipShare, PublicClipShareCursor} from '@/models/clipShare'
import {useAuthStore} from '@/stores/auth'
import {formatBytes, formatDate} from '@/utilities/format'

const {t} = useI18n()
const route = useRoute()
const auth = useAuthStore()
const userId = String(route.params.userId)
const sharesHeadingId = useId()
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

.profile-shares {
  display: flex;
  flex-direction: column;
  gap: var(--ui-layout-gap);
}

.profile-share-grid {
  list-style: none;
  margin: 0;
  padding: 0;
}

.profile-share-grid > li {
  display: flex;
  min-width: 0;
}
</style>
