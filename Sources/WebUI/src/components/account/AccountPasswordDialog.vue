<template>
  <v-dialog v-model="opened" max-width="520" :persistent="busy" :aria-labelledby="titleId" @after-leave="clear">
    <ContentCard :title="t('shell.account.changePassword')" :title-id="titleId">
      <form :id="formId" class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="currentPassword" :label="t('shell.account.currentPassword')" type="password"
                      autocomplete="current-password" required maxlength="256" :disabled="busy"/>
        <v-text-field v-model="password" :label="t('passwordReset.newPassword')" type="password"
                      autocomplete="new-password" required minlength="12" maxlength="256" :disabled="busy"/>
        <v-text-field v-model="confirmation" :label="t('passwordReset.confirmPassword')" type="password"
                      autocomplete="new-password" required minlength="12" maxlength="256" :disabled="busy"/>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('views.common.cancel') }}</v-btn>
        <v-btn type="submit" :form="formId" color="primary" :loading="busy">{{ t('views.common.save') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {ref, useId} from 'vue'
import {useI18n} from 'vue-i18n'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAction} from '@/composables/useAction'
import ContentCard from '@/components/common/ContentCard.vue'

const opened = defineModel<boolean>({required: true})
const emit = defineEmits<{ changed: [] }>()
const {t} = useI18n()
const titleId = useId()
const formId = useId()
const currentPassword = ref('')
const password = ref('')
const confirmation = ref('')
const {busy, error, run} = useAction()

function clear(): void {
  currentPassword.value = ''
  password.value = ''
  confirmation.value = ''
  error.value = ''
}

async function submit(): Promise<void> {
  if (busy.value) {
    return
  }
  await run(async () => {
    if (password.value !== confirmation.value) {
      throw new Error(t('passwordReset.mismatch'))
    }
    await AuthAPI.changePassword(currentPassword.value, password.value)
    clear()
    opened.value = false
    emit('changed')
  })
}
</script>
