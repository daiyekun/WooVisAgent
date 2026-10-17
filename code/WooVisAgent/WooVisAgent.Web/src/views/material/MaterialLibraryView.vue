<script setup lang="ts">
import { NCheckbox, NModal, NPagination, NSpin } from 'naive-ui'
import { storeToRefs } from 'pinia'
import { onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import VideoPlayer from '@/components/VideoPlayer.vue'
import { PAGE_SIZE_OPTIONS } from '@/constants'
import { useMaterialStore } from '@/stores/materialStore'
import type { MaterialAsset } from '@/types'
import { copyText, dialog, downloadUrl, formatDateTime, formatFileSize, message } from '@/utils'

const router = useRouter()
const route = useRoute()
const materialStore = useMaterialStore()

/**
 * 这里必须用 storeToRefs，而不是 `const materials = materialStore.materials`。
 *
 * 后者只在 setup 那一刻把 store 里的数组取出来存进局部变量，
 * 之后 store 内部 `materials.value = result.items` 换的是 store 的引用，
 * 局部变量仍指向旧数组——表现就是「点了筛选没反应，列表纹丝不动」。
 * storeToRefs 保持与 store 的双向链接，筛选结果才会真正渲染出来。
 */
const { loading, materials, total, groups } = storeToRefs(materialStore)

const keyword = ref('')
const typeFilter = ref<'image' | 'video' | null>(null)
const groupFilter = ref<string | null>(null)

const previewVisible = ref(false)
const previewAsset = ref<MaterialAsset | null>(null)

function loadData(): Promise<void> {
  return materialStore.loadMaterials()
}

/** 筛选条件都在本组件的 ref 里，查询前先同步进 store 的 query */
function applyFilters(): Promise<void> {
  materialStore.query.keyword = keyword.value.trim() || undefined
  materialStore.query.type = typeFilter.value ?? undefined
  materialStore.query.groupName = groupFilter.value ?? undefined
  return materialStore.search()
}

function setType(value: 'image' | 'video' | null): void {
  typeFilter.value = value
  void applyFilters()
}

function setGroup(value: string | null): void {
  groupFilter.value = value
  void applyFilters()
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

/**
 * 删除作品。
 *
 * store 内部会同步把总数 -1，侧边栏角标立刻跟着变；
 * 再 reloadAfterRemove 处理「删完当前页空了一页」的情况。
 */
function handleRemove(asset: MaterialAsset): void {
  dialog.error({
    title: '删除素材',
    content: `确认删除「${asset.fileName || asset.id}」？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await materialStore.removeMaterial(asset.id)
      message.success('已删除')
      await materialStore.reloadAfterRemove()
    },
  })
}

function handleBatchRemove(): void {
  const ids = [...materialStore.selectedIds]
  if (!ids.length) return

  dialog.error({
    title: '批量删除',
    content: `确认删除选中的 ${ids.length} 个素材？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await materialStore.removeMaterials(ids)
      message.success(`已删除 ${ids.length} 个素材`)
      await materialStore.reloadAfterRemove()
    },
  })
}

onMounted(async () => {
  await Promise.all([materialStore.loadGroups(), loadData()])
})

/**
 * 离开作品库时把总数同步给侧边栏角标。
 *
 * 列表查询已经把 total 写回 store，这一步只是保证万一某条路径只增删了
 * local 状态（比如别处直接调了 bump），角标也能在下次进入时校准。
 */
watch(
  () => route.name,
  (name, previous) => {
    if (previous === 'Works' && name !== 'Works') {
      void materialStore.loadTotal()
    }
  },
)
</script>

<template>
  <div>
    <!-- 页面标题条 -->
    <div class="page-head">
      <h1>
        我的作品
        <small>共 {{ total }} 个作品 · 在任务列表点「选秀」即可把生成结果收录到这里</small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <button class="btn btn-ghost btn-sm" type="button" :disabled="loading" @click="loadData">
          <app-icon name="refresh" :size="15" />
          刷新
        </button>
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
        :checked="materialStore.allSelected"
        @update:checked="materialStore.toggleSelectAll"
      >
        全选
      </n-checkbox>

      <button
        v-if="materialStore.selectedIds.length"
        class="btn btn-danger btn-sm"
        @click="handleBatchRemove"
      >
        <app-icon name="trash" :size="14" />
        删除选中({{ materialStore.selectedIds.length }})
      </button>

      <div class="search">
        <span class="search-icon"><app-icon name="search" :size="15" /></span>
        <input v-model="keyword" placeholder="搜索文件名" @keyup.enter="applyFilters" />
      </div>
    </div>

    <!-- 素材网格 -->
    <n-spin :show="loading">
      <div v-if="materials.length" class="works">
        <div
          v-for="asset in materials"
          :key="asset.id"
          class="work"
          :class="{ 'is-selected': materialStore.isSelected(asset.id) }"
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
                :checked="materialStore.isSelected(asset.id)"
                @update:checked="(value: boolean) => materialStore.toggleSelect(asset.id, value)"
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
            <div class="t">{{ asset.fileName || asset.sourceTaskName || '未命名作品' }}</div>
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
        title="还没有作品"
        description="去任务列表找到已完成的生成结果，点「选秀」即可收录到这里"
      >
        <button class="btn btn-primary btn-sm" type="button" @click="router.push({ name: 'TaskList' })">
          <app-icon name="list" :size="15" />
          去任务列表
        </button>
      </empty-state>

      <div v-else class="py-24" />
    </n-spin>

    <!-- 分页 -->
    <div v-if="total > 0" class="flex justify-end pt-5">
      <n-pagination
        v-model:page="materialStore.query.page"
        v-model:page-size="materialStore.query.pageSize"
        :item-count="total"
        :page-sizes="PAGE_SIZE_OPTIONS"
        show-size-picker
        show-quick-jumper
        @update:page="loadData"
        @update:page-size="
          () => {
            materialStore.query.page = 1
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

        <div class="mt-3 flex flex-wrap items-center gap-3 text-xs t-dim">
          <span v-if="previewAsset.width && previewAsset.height">
            {{ previewAsset.width }} × {{ previewAsset.height }}
          </span>
          <span v-if="previewAsset.fileSize">{{ formatFileSize(previewAsset.fileSize) }}</span>
          <span v-if="previewAsset.sourceTaskName">来自任务「{{ previewAsset.sourceTaskName }}」</span>
        </div>
      </template>
    </n-modal>
  </div>
</template>
