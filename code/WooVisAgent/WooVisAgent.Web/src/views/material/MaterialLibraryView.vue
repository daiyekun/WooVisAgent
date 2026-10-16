<script setup lang="ts">
import { NButton, NCheckbox, NModal, NPagination, NSpin, NUpload, type UploadCustomRequestOptions } from 'naive-ui'
import { computed, onMounted, ref } from 'vue'

import { materialApi } from '@/api'
import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import VideoPlayer from '@/components/VideoPlayer.vue'
import { PAGE_SIZE_OPTIONS } from '@/constants'
import type { MaterialAsset } from '@/types'
import { copyText, dialog, downloadUrl, formatDateTime, formatFileSize, message } from '@/utils'

const loading = ref(false)
const materials = ref<MaterialAsset[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(24)

const keyword = ref('')
const typeFilter = ref<'image' | 'video' | null>(null)
const groupFilter = ref<string | null>(null)
const groups = ref<string[]>([])

const selectedIds = ref<string[]>([])

const previewVisible = ref(false)
const previewAsset = ref<MaterialAsset | null>(null)

const allSelected = computed(
  () => materials.value.length > 0 && selectedIds.value.length === materials.value.length,
)

function isSelected(id: string): boolean {
  return selectedIds.value.includes(id)
}

function toggleSelect(id: string, checked: boolean): void {
  selectedIds.value = checked
    ? [...selectedIds.value, id]
    : selectedIds.value.filter((item) => item !== id)
}

function toggleSelectAll(checked: boolean): void {
  selectedIds.value = checked ? materials.value.map((item) => item.id) : []
}

async function loadData(): Promise<void> {
  loading.value = true
  try {
    const result = await materialApi.fetchMaterials({
      page: page.value,
      pageSize: pageSize.value,
      keyword: keyword.value.trim(),
      type: typeFilter.value ?? undefined,
      groupName: groupFilter.value ?? undefined,
    })
    materials.value = result.items ?? []
    total.value = result.total ?? 0
  } catch {
    materials.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

async function loadGroups(): Promise<void> {
  try {
    groups.value = await materialApi.fetchMaterialGroups()
  } catch {
    groups.value = []
  }
}

function search(): void {
  page.value = 1
  void loadData()
}

function setType(value: 'image' | 'video' | null): void {
  typeFilter.value = value
  search()
}

function setGroup(value: string | null): void {
  groupFilter.value = value
  search()
}

/** 覆盖 NaiveUI 默认的 XHR 上传，改走统一封装的 http 客户端 */
async function handleUpload(options: UploadCustomRequestOptions): Promise<void> {
  const raw = options.file.file
  if (!raw) {
    options.onError()
    return
  }

  try {
    await materialApi.uploadMaterial(raw, groupFilter.value ?? undefined)
    options.onFinish()
    message.success(`${options.file.name} 上传成功`)
    await loadGroups()
    await loadData()
  } catch {
    options.onError()
  }
}

function handlePreview(asset: MaterialAsset): void {
  previewAsset.value = asset
  previewVisible.value = true
}

function handleDownload(asset: MaterialAsset): void {
  downloadUrl(asset.url, asset.fileName)
}

async function handleCopyUrl(asset: MaterialAsset): Promise<void> {
  const ok = await copyText(asset.url)
  ok ? message.success('地址已复制') : message.error('复制失败')
}

function handleRemove(asset: MaterialAsset): void {
  dialog.error({
    title: '删除素材',
    content: `确认删除「${asset.fileName || asset.id}」？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await materialApi.deleteMaterial(asset.id)
      selectedIds.value = selectedIds.value.filter((item) => item !== asset.id)
      message.success('已删除')
      await loadData()
    },
  })
}

function handleBatchRemove(): void {
  const ids = [...selectedIds.value]
  if (!ids.length) return

  dialog.error({
    title: '批量删除',
    content: `确认删除选中的 ${ids.length} 个素材？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await materialApi.batchDeleteMaterials(ids)
      selectedIds.value = []
      message.success(`已删除 ${ids.length} 个素材`)
      await loadData()
    },
  })
}

onMounted(async () => {
  await Promise.all([loadGroups(), loadData()])
})
</script>

<template>
  <div>
    <!-- 页面标题条 -->
    <div class="page-head">
      <h1>
        素材库
        <small>共 {{ total }} 个素材 · 上传或归档的图片与视频都会出现在这里</small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <n-button size="small" quaternary :loading="loading" @click="loadData">
          <template #icon><app-icon name="refresh" :size="15" /></template>
          刷新
        </n-button>

        <n-upload
          :show-file-list="false"
          multiple
          accept="image/*,video/*"
          :custom-request="handleUpload"
        >
          <button class="btn btn-primary btn-sm" type="button">
            <app-icon name="upload" :size="15" />
            上传素材
          </button>
        </n-upload>
      </div>
    </div>

    <!-- 筛选 -->
    <div class="filters">
      <div class="group">
        <button class="pill" :class="{ on: typeFilter === null }" @click="setType(null)">
          全部类型
        </button>
        <button class="pill" :class="{ on: typeFilter === 'image' }" @click="setType('image')">
          图片
        </button>
        <button class="pill" :class="{ on: typeFilter === 'video' }" @click="setType('video')">
          视频
        </button>
      </div>

      <div v-if="groups.length" class="group">
        <button class="pill" :class="{ on: groupFilter === null }" @click="setGroup(null)">
          全部分组
        </button>
        <button
          v-for="item in groups"
          :key="item"
          class="pill"
          :class="{ on: groupFilter === item }"
          @click="setGroup(item)"
        >
          {{ item }}
        </button>
      </div>

      <div class="spacer" />

      <n-checkbox
        v-if="materials.length"
        :checked="allSelected"
        @update:checked="toggleSelectAll"
      >
        全选
      </n-checkbox>

      <button v-if="selectedIds.length" class="btn btn-danger btn-sm" @click="handleBatchRemove">
        <app-icon name="trash" :size="14" />
        删除选中({{ selectedIds.length }})
      </button>

      <div class="search">
        <span class="search-icon"><app-icon name="search" :size="15" /></span>
        <input v-model="keyword" placeholder="搜索文件名" @keyup.enter="search" />
      </div>
    </div>

    <!-- 素材网格 -->
    <n-spin :show="loading">
      <div v-if="materials.length" class="works">
        <div
          v-for="asset in materials"
          :key="asset.id"
          class="work"
          :class="{ 'is-selected': isSelected(asset.id) }"
        >
          <div class="pv">
            <img
              v-if="asset.type === 'image'"
              :src="asset.thumbnailUrl || asset.url"
              :alt="asset.fileName"
              loading="lazy"
            />
            <span v-else class="grid h-full w-full place-items-center t-dim">
              <app-icon name="video" :size="30" />
            </span>

            <span class="kind" :class="{ vid: asset.type === 'video' }">
              <app-icon :name="asset.type === 'video' ? 'video' : 'image'" :size="10" />
              {{ asset.type === 'video' ? '视频' : '图片' }}
            </span>

            <span class="pick">
              <n-checkbox
                :checked="isSelected(asset.id)"
                @update:checked="(value: boolean) => toggleSelect(asset.id, value)"
              />
            </span>

            <div class="ov">
              <button class="act" type="button" title="预览" @click="handlePreview(asset)">
                <app-icon name="search" :size="15" />
              </button>
              <button class="act" type="button" title="下载" @click="handleDownload(asset)">
                <app-icon name="download" :size="15" />
              </button>
              <button class="act" type="button" title="复制链接" @click="handleCopyUrl(asset)">
                <app-icon name="link" :size="15" />
              </button>
              <button class="act" type="button" title="删除" @click="handleRemove(asset)">
                <app-icon name="trash" :size="15" />
              </button>
            </div>
          </div>

          <div class="info">
            <div class="t">{{ asset.fileName || asset.id }}</div>
            <div class="m">
              <span>{{ formatFileSize(asset.fileSize) }}</span>
              <span class="dot" />
              <span>{{ formatDateTime(asset.createdAt, 'MM-DD HH:mm') }}</span>
              <span v-if="asset.groupName" class="tag tag-dim">{{ asset.groupName }}</span>
            </div>
          </div>
        </div>
      </div>

      <empty-state
        v-else-if="!loading"
        icon="images"
        title="素材库还是空的"
        description="上传图片或视频，或把任务生成的结果归档到这里"
      />

      <div v-else class="py-24" />
    </n-spin>

    <!-- 分页 -->
    <div v-if="total > 0" class="flex justify-end pt-5">
      <n-pagination
        v-model:page="page"
        v-model:page-size="pageSize"
        :item-count="total"
        :page-sizes="PAGE_SIZE_OPTIONS"
        show-size-picker
        show-quick-jumper
        @update:page="loadData"
        @update:page-size="
          () => {
            page = 1
            loadData()
          }
        "
      />
    </div>

    <!-- 预览 -->
    <n-modal
      v-model:show="previewVisible"
      preset="card"
      :title="previewAsset?.fileName || '素材预览'"
      style="max-width: 900px"
      :bordered="false"
    >
      <template v-if="previewAsset">
        <img
          v-if="previewAsset.type === 'image'"
          :src="previewAsset.url"
          :alt="previewAsset.fileName"
          class="mx-auto max-h-[65vh] rounded-lg object-contain"
        />
        <video-player
          v-else
          :src="previewAsset.url"
          :poster="previewAsset.thumbnailUrl"
          :duration="previewAsset.duration"
          :file-size="previewAsset.fileSize"
          :file-name="previewAsset.fileName"
        />
      </template>
    </n-modal>
  </div>
</template>
