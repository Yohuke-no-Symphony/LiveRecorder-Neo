<template>
  <PageHeader :title="t('shell.account.title')"/>
  <v-alert v-if="error || saveError" type="error" class="mb-6">{{ error || saveError }}</v-alert>
  <div class="account-settings">
    <AccountProfileCard/>
    <ContentCard :title="t('shell.account.security')">
      <v-progress-linear v-if="!settings && busy" indeterminate color="primary"/>
      <v-btn v-else-if="!settings" variant="text" @click="load">{{ t('views.common.retry') }}</v-btn>
      <div v-else class="form-stack">
        <div class="security-row">
          <div>
            <h3 class="text-body-2 font-weight-medium">{{ t('views.common.email') }}</h3>
            <div class="text-body-2 muted">{{ auth.user?.email }}</div>
          </div>
          <v-btn variant="tonal" :disabled="!settings.canChangeEmail" @click="emailOpen = true">
            {{ t('shell.account.changeEmail') }}
          </v-btn>
        </div>
        <p v-if="!settings.canChangeEmail" class="text-body-2 muted">{{ t('shell.account.smtpUnavailable') }}</p>
        <div class="security-row">
          <h3 class="text-body-2 font-weight-medium">{{ t('views.common.password') }}</h3>
          <v-btn v-if="settings.hasLocalPassword" variant="tonal" @click="passwordOpen = true">
            {{ t('shell.account.changePassword') }}
          </v-btn>
          <v-btn v-else variant="tonal" to="/forgot-password">{{ t('shell.account.setPassword') }}</v-btn>
        </div>
      </div>
    </ContentCard>
    <ContentCard :title="t('shell.account.language')">
      <LanguageSelect :model-value="auth.user?.language ?? null" allow-default :disabled="saving" :loading="saving"
                      @update:model-value="saveLanguage"/>
    </ContentCard>
    <ContentCard :title="t('shell.account.loginMethods')">
      <v-progress-linear v-if="busy && !providers.length" indeterminate color="primary"/>
      <v-list v-if="providers.length" bg-color="transparent">
        <v-list-item v-for="provider in providers" :key="provider.id" :title="provider.name"
                     prepend-icon="mdi-account-key-outline">
          <template #append>
            <v-btn :variant="boundIds.includes(provider.id) ? 'text' : 'tonal'"
                   :color="boundIds.includes(provider.id) ? 'success' : 'primary'"
                   :disabled="boundIds.includes(provider.id) || !!bindingId" :loading="bindingId === provider.id"
                   :prepend-icon="boundIds.includes(provider.id) ? 'mdi-check' : 'mdi-link-variant'"
                   @click="bind(provider.id)">{{ boundIds.includes(provider.id) ? t('shell.account.bound') :
              t('shell.account.bind') }}
            </v-btn>
          </template>
        </v-list-item>
      </v-list>
      <EmptyState v-else-if="!busy" :title="t('shell.account.noProviders')" icon="mdi-account-key-outline"/>
    </ContentCard>
  </div>
  <AccountPasswordDialog v-model="passwordOpen" @changed="relogin('password')"/>
  <AccountEmailDialog v-if="settings" v-model="emailOpen" :settings="settings" :providers="boundProviders"
                      @challenge="rememberChallenge" @changed="relogin('email')"/>
  <v-snackbar v-model="saved" color="success">{{ t('shell.account.languageSaved') }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, onMounted, ref} from 'vue'
import ContentCard from '@/components/common/ContentCard.vue'
import {useI18n} from 'vue-i18n'
import {useRoute, useRouter} from 'vue-router'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'
import type {AppLanguage} from '@/localization/types'
import type {AccountSettings, PendingEmailChange} from '@/models/auth'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LanguageSelect from '@/components/common/LanguageSelect.vue'
import AccountProfileCard from '@/components/account/AccountProfileCard.vue'
import AccountPasswordDialog from '@/components/account/AccountPasswordDialog.vue'
import AccountEmailDialog from '@/components/account/AccountEmailDialog.vue'

const {t} = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const providers = ref<{ id: string; name: string }[]>([])
const boundIds = ref<string[]>([])
const boundProviders = computed(() => providers.value.filter(provider => boundIds.value.includes(provider.id)))
const settings = ref<AccountSettings>()
const passwordOpen = ref(false)
const emailOpen = ref(false)
const bindingId = ref('')
const saved = ref(false)
const {busy, error, run} = useAction()
const {busy: saving, error: saveError, run: runSave} = useAction()
onMounted(load)

async function load(): Promise<void> {
  await run(async () => {
    [providers.value, boundIds.value, settings.value] = await Promise.all([
      AuthAPI.providers(), AuthAPI.identities(), AuthAPI.accountSettings(),
    ])
    if (route.query.emailChange === 'sent') {
      emailOpen.value = !!settings.value.pendingEmailChange
      await router.replace({path: '/account'})
    }
  })
}

function rememberChallenge(pending: PendingEmailChange): void {
  if (settings.value) {
    settings.value.pendingEmailChange = pending
  }
}

async function relogin(change: 'email' | 'password'): Promise<void> {
  const target = {path: '/login', query: {accountChanged: change}}
  auth.clearSession()
  try {
    await auth.refresh()
    await router.replace(target)
  } catch {
    window.location.assign(router.resolve(target).href)
  }
}

async function saveLanguage(language: AppLanguage | null): Promise<void> {
  await runSave(async () => {
    await auth.setLanguage(language)
    saved.value = true
  })
}

async function bind(id: string): Promise<void> {
  bindingId.value = id
  await run(async () => {
    const result = await AuthAPI.oauthBind(id)
    sessionStorage.setItem('oauth_return_to', '/account')
    window.location.assign(result.authorizationUrl)
  })
  bindingId.value = ''
}
</script>

<style scoped>
.account-settings {
  display: grid;
  gap: var(--ui-layout-gap);
  max-width: 720px;
}

.security-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  overflow-wrap: anywhere;
}
</style>
