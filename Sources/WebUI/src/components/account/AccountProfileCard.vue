<template>
  <ContentCard :title="t('shell.account.profile')">
    <div class="profile-identity">
      <AvatarCropDialog v-model="cropOpen" @saved="notify('shell.account.avatar.saved')" @cleared="notify('shell.account.avatar.cleared')">
        <template #activator="{ props }">
          <button v-bind="props" class="profile-avatar-button" type="button" :aria-label="t('shell.account.avatar.change')">
            <UserAvatar :name="currentName" :avatar-url="auth.user?.avatarUrl" :size="56"/>
            <span class="profile-avatar-overlay" aria-hidden="true"><v-icon icon="mdi-pencil-outline"/></span>
          </button>
        </template>
      </AvatarCropDialog>
      <form class="profile-name" @submit.prevent="save" @keydown.esc="cancelNameEdit">
        <v-text-field v-if="editingName" v-model="displayName" :label="t('views.register.displayName')" required maxlength="80"
                      autocomplete="nickname" :disabled="busy" class="profile-name-input" density="compact" hide-details autofocus/>
        <span v-else class="profile-name-text" :title="currentName">{{ currentName }}</span>
        <v-btn v-if="editingName" type="submit" color="primary" variant="text" size="small" class="profile-name-action"
               icon="mdi-content-save-outline" :title="t('views.common.save')" :aria-label="t('views.common.save')"
               :disabled="!displayName.trim()" :loading="busy"/>
        <v-btn v-else ref="editAction" type="button" variant="text" size="small" class="profile-name-action"
               icon="mdi-pencil-outline" :title="t('shell.account.editDisplayName')" :aria-label="t('shell.account.editDisplayName')"
               @click="editName"/>
      </form>
    </div>
    <v-alert v-if="error" class="mt-3" type="error">{{ error }}</v-alert>
  </ContentCard>
  <v-snackbar v-model="saved" color="success">{{ t(savedMessage) }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, nextTick, ref, useTemplateRef, type ComponentPublicInstance} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import AvatarCropDialog from './AvatarCropDialog.vue'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'

const {t} = useI18n()
const auth = useAuthStore()
const currentName = computed(() => auth.user?.displayName ?? '')
const displayName = ref('')
const editingName = ref(false)
const editAction = useTemplateRef<ComponentPublicInstance>('editAction')
const saved = ref(false)
const savedMessage = ref('shell.account.profileSaved')
const cropOpen = ref(false)
const {busy, error, run} = useAction()

function notify(message: string): void {
  savedMessage.value = message
  saved.value = true
}

function editName(): void {
  displayName.value = currentName.value
  editingName.value = true
}

function finishNameEdit(): void {
  editingName.value = false
  void nextTick(() => editAction.value?.$el.focus())
}

function cancelNameEdit(event: KeyboardEvent): void {
  if (editingName.value && !busy.value) {
    event.preventDefault()
    error.value = ''
    finishNameEdit()
  }
}

async function save(): Promise<void> {
  const name = displayName.value.trim()
  if (!editingName.value || busy.value || !name) {
    return
  }
  if (name !== currentName.value) {
    const updated = await run(() => auth.setDisplayName(name))
    if (updated === undefined) {
      return
    }
    notify('shell.account.profileSaved')
  }
  error.value = ''
  finishNameEdit()
}
</script>

<style scoped>
.profile-identity,
.profile-name {
  display: flex;
  align-items: center;
  min-width: 0;
}

.profile-identity { gap: 12px; }
.profile-name { flex: 1; gap: 4px; max-width: 28rem; }
.profile-name-input { min-width: 0; }
.profile-name-action { flex-shrink: 0; }

.profile-name-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 1rem;
  font-weight: 600;
}

.profile-avatar-button {
  position: relative;
  display: grid;
  flex-shrink: 0;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: transparent;
  cursor: pointer;
}

.profile-avatar-button:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 3px;
}

.profile-avatar-overlay {
  position: absolute;
  display: grid;
  place-items: center;
  inset: 0;
  border-radius: inherit;
  background: rgba(var(--v-theme-surface), .55);
  color: rgb(var(--v-theme-on-surface));
  -webkit-backdrop-filter: blur(4px);
  backdrop-filter: blur(4px);
  opacity: 0;
  transition: opacity .18s ease;
}

.profile-avatar-button:hover .profile-avatar-overlay,
.profile-avatar-button:focus-visible .profile-avatar-overlay {
  opacity: 1;
}

@media (hover: none) {
  .profile-avatar-overlay {
    inset: auto 0 0 auto;
    width: 24px;
    height: 24px;
    background: rgb(var(--v-theme-surface));
    font-size: 12px;
    opacity: 1;
  }
}
</style>
