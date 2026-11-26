<template>
  <v-dialog v-model="opened" :persistent="busy" :aria-label="t('sharing.clipTitle')">
    <ContentCard :title="t('sharing.clipTitle')">
      <form :id="formId" class="form-stack" @submit.prevent="save">
        <p class="clip-share-title">{{ clip.title }}</p>
        <v-progress-linear v-if="!loaded && busy" indeterminate color="primary"/>
        <template v-if="loaded && auth.canViewRecordings">
          <v-select v-model="visibility" :items="visibilityOptions" :label="t('sharing.visibility')" :disabled="busy"/>
          <p class="text-body-2 muted">{{ t(visibility === 'public' ? 'sharing.publicHint' : 'sharing.privateHint') }}</p>
          <v-text-field v-if="visibility === 'private'" v-model="password" :label="t('sharing.password')"
                        type="password" autocomplete="new-password" :required="requiresPassword" minlength="4" maxlength="64"
                        :hint="requiresPassword ? undefined : t('sharing.keepPassword')" :disabled="busy"/>
          <p v-if="token && changed" class="text-body-2 muted">{{ t('sharing.updateLinkHint') }}</p>
        </template>
        <ShareLinkField v-if="link" :link="manualLink || link"/>
        <p v-if="manualLink" class="text-body-2" role="status">{{ t('sharing.manualCopy') }}</p>
        <v-alert v-if="loaded && !token && !auth.canViewRecordings" type="info">{{ t('sharing.createPermission') }}
        </v-alert>
        <v-alert v-if="error && !confirmRevoke" type="error">
          {{ error }}
          <template v-if="!loaded || conflict" #append>
            <v-btn variant="text" size="small" @click="load">{{ t('views.common.retry') }}</v-btn>
          </template>
        </v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('views.common.close') }}</v-btn>
        <template v-if="token">
          <v-btn color="error" variant="text" :disabled="busy" @click="askRevoke">{{ t('sharing.revoke') }}</v-btn>
          <v-btn color="primary" prepend-icon="mdi-content-copy" :disabled="busy" @click="copy(false)">{{ t('sharing.copy')
            }}
          </v-btn>
          <v-btn v-if="knownPassword" color="primary" variant="tonal" :disabled="busy" @click="copy(true)">
            {{ t('sharing.copyWithPassword') }}
          </v-btn>
        </template>
        <v-btn v-if="loaded && auth.canViewRecordings && changed" type="submit" :form="formId"
               color="primary" :loading="busy">{{ t(token ? 'views.common.save' : 'sharing.create') }}
        </v-btn>
      </template>
    </ContentCard>
  </v-dialog>
  <v-snackbar v-model="linkCopied" location="bottom center" color="surface-variant"
              style="--v-layout-left: 0px; --v-layout-right: 0px" min-width="min(344px, 100%)" _disable-global-stack>
    <div class="text-center">{{ t('sharing.copied') }}</div>
  </v-snackbar>
  <ConfirmDialog v-model="confirmRevoke" :title="t('sharing.revoke')" :message="t('sharing.revokeConfirm')"
                 :action-label="t('sharing.revoke')" :busy="busy" :error="confirmRevoke ? error : ''" danger
                 @confirm="revoke"/>
</template>

<script setup lang="ts">
import {computed, onMounted, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useRouter} from 'vue-router'
import {MediaAPI} from '@/apis/MediaAPI'
import {ApiError} from '@/apis/APIBase'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ContentCard from '@/components/common/ContentCard.vue'
import ShareLinkField from '@/components/common/ShareLinkField.vue'
import {useAction} from '@/composables/useAction'
import type {Clip} from '@/models/media'
import type {ClipShareLink, ClipShareVisibility} from '@/models/clipShare'
import {useAuthStore} from '@/stores/auth'
import {absoluteShareLink, copyShareLink} from '@/utilities/shareLinks'

const props = defineProps<{ clip: Pick<Clip, 'id' | 'title'> }>()
const opened = defineModel<boolean>({required: true})
const {t} = useI18n()
const router = useRouter()
const auth = useAuthStore()
const formId = useId()
const token = ref<string | null>(null)
const savedVisibility = ref<ClipShareVisibility>('public')
const visibility = ref<ClipShareVisibility>('public')
const password = ref('')
const knownPassword = ref('')
const loaded = ref(false)
const conflict = ref(false)
const confirmRevoke = ref(false)
const linkCopied = ref(false)
const manualLink = ref('')
const {busy, error, run} = useAction()
const visibilityOptions = computed(() => [
  {title: t('sharing.public'), value: 'public'},
  {title: t('sharing.private'), value: 'private'},
])
const changed = computed(() => !token.value || visibility.value !== savedVisibility.value ||
  visibility.value === 'private' && password.value.length > 0)
const requiresPassword = computed(() => visibility.value === 'private' && (!token.value || savedVisibility.value !== 'private'))
const link = computed(() => shareUrl())

onMounted(load)
watch(opened, value => {
  if (!value) {
    password.value = ''
    knownPassword.value = ''
    manualLink.value = ''
  }
})

function shareUrl(candidate?: string): string {
  if (!token.value) {
    return ''
  }
  return absoluteShareLink(router.resolve({
    name: 'shared-clip', params: {id: props.clip.id, token: token.value}, query: candidate ? {pwd: candidate} : {},
  }).href)
}

function applySettings(settings: ClipShareLink): void {
  token.value = settings.token
  savedVisibility.value = settings.visibility ?? 'public'
  visibility.value = savedVisibility.value
  password.value = ''
  loaded.value = true
}

async function readSettings(): Promise<void> {
  applySettings(await MediaAPI.clipShare(props.clip.id))
}

async function load(): Promise<void> {
  conflict.value = false
  manualLink.value = ''
  knownPassword.value = ''
  await run(readSettings)
}

async function save(): Promise<void> {
  if (busy.value || !auth.canViewRecordings || !changed.value) {
    return
  }
  conflict.value = false
  await run(async () => {
    const submitted = visibility.value === 'private' && password.value ? password.value : undefined
    const settings = await MediaAPI.saveClipShare(props.clip.id, {
      token: token.value, visibility: visibility.value, password: submitted,
    }).catch(cause => {
      conflict.value = cause instanceof ApiError && cause.code === 'LIBRARY_CLIP_SHARE_SETTINGS_CONFLICT'
      throw cause
    })
    knownPassword.value = settings.visibility === 'private' ? (submitted ?? knownPassword.value) : ''
    manualLink.value = ''
    linkCopied.value = false
    applySettings(settings)
  })
}

function askRevoke(): void {
  error.value = ''
  confirmRevoke.value = true
}

async function revoke(): Promise<void> {
  const currentToken = token.value
  if (!currentToken) {
    return
  }
  await run(async () => {
    await MediaAPI.revokeClipShare(props.clip.id, currentToken)
    token.value = null
    loaded.value = false
    confirmRevoke.value = false
    linkCopied.value = false
    manualLink.value = ''
    knownPassword.value = ''
    await readSettings()
  })
}

async function copy(withPassword: boolean): Promise<void> {
  const currentToken = token.value
  const currentLink = shareUrl(withPassword ? knownPassword.value : undefined)
  const copied = await copyShareLink(currentLink)
  if (opened.value && currentToken === token.value) {
    linkCopied.value = copied
    manualLink.value = copied ? '' : currentLink
  }
}
</script>

<style scoped>
.clip-share-title {
  overflow-wrap: anywhere;
}
</style>
