<template>
  <v-dialog v-model="opened" max-width="520" :persistent="busy" :aria-labelledby="titleId">
    <ContentCard :title="t('shell.account.changeEmail')" :title-id="titleId">
      <form :id="formId" class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="email" :label="t('shell.account.newEmail')" type="email" autocomplete="email"
                      required maxlength="254" :disabled="busy"/>
        <template v-if="waitingForCode">
          <v-text-field v-model="code" :label="t('components.common.emailCode')" inputmode="numeric"
                        autocomplete="one-time-code" required pattern="[0-9]{6}" maxlength="6" :disabled="busy"/>
          <div class="d-flex align-center flex-wrap ga-3">
            <span class="text-body-2 muted" role="status">{{ t('shell.account.emailCodeSent') }}</span>
            <v-btn type="button" variant="text" color="primary" :disabled="busy || cooldown > 0" @click="resend">
              {{ cooldown > 0 ? t('components.common.countdown', { seconds: cooldown }) : t('shell.account.resendCode') }}
            </v-btn>
          </div>
        </template>
        <v-text-field v-else-if="settings.hasLocalPassword" v-model="currentPassword"
                      :label="t('shell.account.currentPassword')" type="password" autocomplete="current-password"
                      required maxlength="256" :disabled="busy"/>
        <v-select v-else-if="providers.length" v-model="providerId" :items="providers" item-title="name" item-value="id"
                  :label="t('shell.account.loginMethods')" :disabled="busy"/>
        <v-alert v-else type="info">{{ t('shell.account.noBoundProvider') }}</v-alert>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('views.common.cancel') }}</v-btn>
        <v-btn type="submit" :form="formId" color="primary" :loading="busy"
               :disabled="!waitingForCode && !settings.hasLocalPassword && !providerId">{{ submitLabel }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {computed, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAction} from '@/composables/useAction'
import type {AccountSettings, PendingEmailChange} from '@/models/auth'
import ContentCard from '@/components/common/ContentCard.vue'

const props = defineProps<{ settings: AccountSettings; providers: { id: string; name: string }[] }>()
const opened = defineModel<boolean>({required: true})
const emit = defineEmits<{ changed: []; challenge: [pending: PendingEmailChange] }>()
const {t} = useI18n()
const titleId = useId()
const formId = useId()
const email = ref('')
const currentPassword = ref('')
const code = ref('')
const providerId = ref('')
const now = ref(Date.now())
const {busy, error, run} = useAction()
const address = computed(() => email.value.trim().toLowerCase())
const pending = computed(() => props.settings.pendingEmailChange?.email === address.value ? props.settings.pendingEmailChange : null)
const waitingForCode = computed(() => !!pending.value && Date.parse(pending.value.expiresAtUtc) > now.value)
const cooldown = computed(() => pending.value ? Math.max(0, Math.ceil((Date.parse(pending.value.nextSendAtUtc) - now.value) / 1000)) : 0)
const submitLabel = computed(() => t(waitingForCode.value ? 'views.common.save'
  : props.settings.hasLocalPassword ? 'components.common.sendCode' : 'shell.account.reauthorize'))

watch(opened, (value, _previous, onCleanup) => {
  currentPassword.value = ''
  code.value = ''
  error.value = ''
  if (value) {
    email.value = props.settings.pendingEmailChange?.email ?? ''
    providerId.value = props.providers[0]?.id ?? ''
    now.value = Date.now()
    const timer = window.setInterval(() => { now.value = Date.now() }, 1000)
    onCleanup(() => window.clearInterval(timer))
  }
}, {immediate: true})

watch(address, () => { code.value = '' })

async function requestCode(password?: string): Promise<void> {
  const challenge = await AuthAPI.requestEmailChange(address.value, password)
  currentPassword.value = ''
  code.value = ''
  emit('challenge', challenge)
}

async function submit(): Promise<void> {
  if (busy.value || !address.value) {
    return
  }
  await run(async () => {
    if (waitingForCode.value) {
      if (!/^[0-9]{6}$/.test(code.value)) {
        return
      }
      await AuthAPI.confirmEmailChange(address.value, code.value)
      opened.value = false
      emit('changed')
    } else if (props.settings.hasLocalPassword) {
      await requestCode(currentPassword.value)
    } else if (providerId.value) {
      const result = await AuthAPI.oauthEmailChange(providerId.value, address.value)
      sessionStorage.setItem('oauth_return_to', '/account')
      window.location.assign(result.authorizationUrl)
    }
  })
}

async function resend(): Promise<void> {
  if (!busy.value && waitingForCode.value && cooldown.value === 0) {
    await run(() => requestCode())
  }
}
</script>
