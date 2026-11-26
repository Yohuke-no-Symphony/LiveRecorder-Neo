<template>
  <ContentCard :title="t('shell.account.profile')">
    <form :id="formId" class="form-stack" @submit.prevent="save">
      <div class="d-flex align-center flex-wrap ga-3">
        <UserAvatar :name="auth.user?.displayName ?? ''" :avatar-url="auth.user?.avatarUrl" :size="56"/>
        <v-btn type="button" variant="tonal" @click="cropOpen = true">{{ t('shell.account.avatar.change') }}</v-btn>
        <v-btn v-if="auth.user?.avatarUrl" type="button" variant="text" @click="clearOpen = true">
          {{ t('shell.account.avatar.clear') }}
        </v-btn>
      </div>
      <v-text-field v-model="displayName" :label="t('views.register.displayName')" required maxlength="80"
                    autocomplete="nickname" :disabled="busy"/>
      <v-alert v-if="error" type="error">{{ error }}</v-alert>
    </form>
    <template #actions>
      <v-btn type="submit" :form="formId" color="primary" :disabled="!changed" :loading="busy">
        {{ t('views.common.save') }}
      </v-btn>
    </template>
  </ContentCard>
  <AvatarCropDialog v-model="cropOpen" @saved="notify('shell.account.avatar.saved')"/>
  <ConfirmDialog v-model="clearOpen" :title="t('shell.account.avatar.clear')" :message="t('shell.account.avatar.clearConfirm')"
                 :action-label="t('shell.account.avatar.clear')" :busy="clearing" :error="clearError" danger
                 @confirm="clearAvatar"/>
  <v-snackbar v-model="saved" color="success">{{ t(savedMessage) }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import AvatarCropDialog from './AvatarCropDialog.vue'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'

const {t} = useI18n()
const auth = useAuthStore()
const formId = useId()
const displayName = ref(auth.user?.displayName ?? '')
const saved = ref(false)
const savedMessage = ref('shell.account.profileSaved')
const cropOpen = ref(false)
const clearOpen = ref(false)
const changed = computed(() => displayName.value.trim() !== (auth.user?.displayName ?? '') && !!displayName.value.trim())
const {busy, error, run} = useAction()
const {busy: clearing, error: clearError, run: runClear} = useAction()

function notify(message: string): void {
  savedMessage.value = message
  saved.value = true
}

watch(() => auth.user?.displayName, (name, previous) => {
  if (displayName.value === previous) {
    displayName.value = name ?? ''
  }
})

async function save(): Promise<void> {
  if (!busy.value && changed.value) {
    await run(async () => {
      displayName.value = await auth.setDisplayName(displayName.value.trim())
      notify('shell.account.profileSaved')
    })
  }
}

async function clearAvatar(): Promise<void> {
  if (!clearing.value) {
    await runClear(async () => {
      await auth.changeAvatar(null)
      clearOpen.value = false
      notify('shell.account.avatar.cleared')
    })
  }
}
</script>
