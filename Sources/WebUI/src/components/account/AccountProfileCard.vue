<template>
  <ContentCard :title="t('shell.account.profile')">
    <form :id="formId" class="form-stack" @submit.prevent="save">
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
  <v-snackbar v-model="saved" color="success">{{ t('shell.account.profileSaved') }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'

const {t} = useI18n()
const auth = useAuthStore()
const formId = useId()
const displayName = ref(auth.user?.displayName ?? '')
const saved = ref(false)
const changed = computed(() => displayName.value.trim() !== (auth.user?.displayName ?? '') && !!displayName.value.trim())
const {busy, error, run} = useAction()

watch(() => auth.user?.displayName, (name, previous) => {
  if (displayName.value === previous) {
    displayName.value = name ?? ''
  }
})

async function save(): Promise<void> {
  if (!busy.value && changed.value) {
    await run(async () => {
      displayName.value = await auth.setDisplayName(displayName.value.trim())
      saved.value = true
    })
  }
}
</script>
