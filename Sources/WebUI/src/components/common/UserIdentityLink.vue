<template>
  <component :is="destination ? RouterLink : 'span'" :to="destination" class="user-identity" @click.stop>
    <UserAvatar :name="name" :avatar-url="avatarUrl" :size="size"/>
    <span class="user-identity-name" :title="name">{{ name }}</span>
  </component>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {RouterLink} from 'vue-router'
import UserAvatar from './UserAvatar.vue'

const props = defineProps<{ userId?: string | null; name: string; avatarUrl?: string | null; size?: number | string }>()
const destination = computed(() => props.userId ? {name: 'user-profile', params: {userId: props.userId}} : undefined)
</script>

<style scoped>
.user-identity {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  max-width: 100%;
}

a.user-identity {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
}

a.user-identity:hover .user-identity-name,
a.user-identity:focus-visible .user-identity-name {
  text-decoration: underline;
}

.user-identity-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
