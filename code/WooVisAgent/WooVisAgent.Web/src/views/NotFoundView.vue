<script setup lang="ts">
import { useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'

const router = useRouter()
</script>

<template>
  <div class="grid min-h-[70vh] place-items-center">
    <div class="empty">
      <span class="ico">
        <app-icon name="alert" :size="24" />
      </span>
      <b>404 · 页面不存在</b>
      <p>地址可能已变更，或者你输入了错误的链接</p>
      <button
        class="btn btn-primary btn-sm mt-2"
        type="button"
        @click="router.push({ name: 'Workspace' })"
      >
        返回工作台
      </button>
    </div>
  </div>
</template>
