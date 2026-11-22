<template>
  <v-dialog v-model="opened" :persistent="busy" :aria-label="t('sharing.clipTitle')">
    <ContentCard :title="t('sharing.clipTitle')">
      <div class="form-stack">
        <p class="clip-share-title">{{ clip.title }}</p>
        <v-progress-linear v-if="!loaded && busy" indeterminate color="primary" />
        <ShareLinkField v-if="link" :link="link" />
        <p v-if="copyStatus" class="text-body-2" role="status">{{ t(copyStatus) }}</p>
        <v-alert v-if="loaded && !token && !auth.canViewRecordings" type="info">{{ t('sharing.createPermission') }}</v-alert>
        <v-alert v-if="error && !confirmRevoke" type="error">
          {{ error }}
          <template v-if="!loaded" #append><v-btn variant="text" size="small" @click="load">{{ t('views.common.retry') }}</v-btn></template>
        </v-alert>
      </div>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('views.common.close') }}</v-btn>
        <template v-if="token">
          <v-btn color="error" variant="text" :disabled="busy" @click="askRevoke">{{ t('sharing.revoke') }}</v-btn>
          <v-btn color="primary" prepend-icon="mdi-content-copy" :disabled="busy" @click="copy">{{ t('sharing.copy') }}</v-btn>
        </template>
        <v-btn v-else-if="loaded && auth.canViewRecordings" color="primary" :loading="busy" @click="create">{{ t('sharing.create') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
  <ConfirmDialog v-model="confirmRevoke" :title="t('sharing.revoke')" :message="t('sharing.revokeConfirm')" :action-label="t('sharing.revoke')" :busy="busy" :error="confirmRevoke ? error : ''" danger @confirm="revoke" />
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ContentCard from '@/components/common/ContentCard.vue'
import ShareLinkField from '@/components/common/ShareLinkField.vue'
import { useAction } from '@/composables/useAction'
import type { Clip } from '@/models/media'
import { useAuthStore } from '@/stores/auth'
import { absoluteShareLink, copyShareLink } from '@/utilities/shareLinks'

const props = defineProps<{ clip: Pick<Clip, 'id' | 'title'> }>()
const opened = defineModel<boolean>({ required: true })
const { t } = useI18n()
const router = useRouter()
const auth = useAuthStore()
const token = ref<string | null>(null)
const loaded = ref(false)
const confirmRevoke = ref(false)
const copyStatus = ref<'sharing.copied' | 'sharing.manualCopy'>()
const { busy, error, run } = useAction()
const link = computed(() => token.value
  ? absoluteShareLink(router.resolve({ name: 'shared-clip', params: { id: props.clip.id, token: token.value } }).href)
  : '')

onMounted(load)

async function readToken(): Promise<void>
{
  token.value = (await MediaAPI.clipShare(props.clip.id)).token
  loaded.value = true
}

async function load(): Promise<void>
{
  await run(readToken)
}

async function create(): Promise<void>
{
  await run(async () => { token.value = (await MediaAPI.createClipShare(props.clip.id)).token })
}

function askRevoke(): void
{
  error.value = ''
  confirmRevoke.value = true
}

async function revoke(): Promise<void>
{
  const currentToken = token.value
  if (!currentToken) { return }
  await run(async () =>
  {
    await MediaAPI.revokeClipShare(props.clip.id, currentToken)
    token.value = null
    loaded.value = false
    confirmRevoke.value = false
    copyStatus.value = undefined
    await readToken()
  })
}

async function copy(): Promise<void>
{
  const currentLink = link.value
  const copied = await copyShareLink(currentLink)
  if (currentLink === link.value)
  {
    copyStatus.value = copied ? 'sharing.copied' : 'sharing.manualCopy'
  }
}
</script>

<style scoped>
.clip-share-title { overflow-wrap: anywhere; }
</style>
