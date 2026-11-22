<template>
  <component :is="profileUrl ? 'a' : 'span'" :href="profileUrl" :target="profileUrl ? '_blank' : undefined"
             :rel="profileUrl ? 'noopener noreferrer' : undefined" :title="name" class="analytics-user-identity">
    <EventAvatar :name="name" :sources="avatarSources"/>
    <span class="analytics-user-name">{{ name }}</span>
  </component>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import EventAvatar from '@/components/events/EventAvatar.vue'
import {audienceAvatarUrl, audienceProfileUrl} from '@/utilities/eventAssets'

const props = defineProps<{ userId: string; userName: string }>()
const {t} = useI18n()
const name = computed(() => props.userName.trim() || t('views.analytics.guest'))
const profileUrl = computed(() => audienceProfileUrl(props.userId))
const avatarSources = computed(() => {
  const avatar = audienceAvatarUrl(props.userId)
  return avatar ? [avatar] : []
})
</script>

<style scoped>
.analytics-user-identity {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  max-width: 100%;
  color: inherit;
  text-decoration: none;
  vertical-align: middle;
}

.analytics-user-name {
  min-width: 0;
  max-width: min(320px, 45vw);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

a.analytics-user-identity:hover .analytics-user-name {
  color: rgb(var(--v-theme-primary));
  text-decoration: underline;
  text-underline-offset: 3px;
}

a.analytics-user-identity:focus-visible {
  border-radius: 4px;
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 4px;
}
</style>
