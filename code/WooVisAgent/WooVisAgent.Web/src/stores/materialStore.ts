import { computed, reactive, ref } from 'vue'
import { defineStore } from 'pinia'

import { materialApi } from '@/api'
import type { MaterialAsset, MaterialQuery } from '@/types'

function createDefaultQuery(): Required<Pick<MaterialQuery, 'page' | 'pageSize'>> & MaterialQuery {
  return {
    page: 1,
    pageSize: 24,
    keyword: undefined,
    type: undefined,
    groupName: undefined,
  }
}

/**
 * 作品库（我的作品）的唯一数据源。
 *
 * 侧边栏角标、工作台的「我的作品」指标、作品库页面三处都从这里读数，
 * 保证同一时刻三个地方显示的是同一个数。
 *
 * 之前侧边栏的角标是从 `taskStore.tasks` 里过滤 succeeded 再累加 outputs.length 算的，
 * 有两个硬伤：
 *   1. `taskStore.tasks` 只有当前分页，任务超过一页后必然少数；
 *   2. 统计的是「已完成任务的产物」，而不是「作品库里真实存在的条目」——
 *      没点选秀的产物会被算进去，已选秀又被删掉的又算不出来。
 * 现在统一以接口返回的 total 为准。
 */
export const useMaterialStore = defineStore('material', () => {
  /** 作品列表与分页（作品库页面用） */
  const materials = ref<MaterialAsset[]>([])
  const total = ref(0)
  const loading = ref(false)
  const query = reactive(createDefaultQuery())

  /** 分组页签 */
  const groups = ref<string[]>([])

  /** 列表勾选 */
  const selectedIds = ref<string[]>([])

  /** 只取数量时的 loading，与列表 loading 分开，避免为一枚角标让整页转圈 */
  const counting = ref(false)
  /** in-flight  promise，防止侧边栏 / 页面 / 路由切换同时触发多次相同请求 */
  let inflightCount: Promise<void> | null = null

  const isEmpty = computed(() => !loading.value && materials.value.length === 0)

  const totalPages = computed(() => {
    const size = query.pageSize || 24
    return Math.max(1, Math.ceil(total.value / size))
  })

  /**
   * 只拉作品总数。
   *
   * pageSize=1：total 由后端单独 COUNT 出来，不会因为只要一个数就把整页素材传回来。
   * 请求失败时保留上一次的数字而不是清零 —— 角标突然闪成 0 比慢一拍更让人困惑，
   * 真正的登录失效会由路由守卫直接跳走，不会停在这个界面上。
   */
  async function loadTotal(): Promise<void> {
    if (inflightCount) return inflightCount

    counting.value = true
    inflightCount = (async () => {
      try {
        const result = await materialApi.fetchMaterials({ page: 1, pageSize: 1 })
        total.value = result.total ?? 0
      } catch {
        // 错误提示已由 http 拦截器统一处理
      } finally {
        counting.value = false
        inflightCount = null
      }
    })()

    return inflightCount
  }

  /** 拉取作品列表（作品库页面） */
  async function loadMaterials(patch: Partial<typeof query> = {}): Promise<void> {
    Object.assign(query, patch)
    loading.value = true
    try {
      const result = await materialApi.fetchMaterials({
        page: query.page,
        pageSize: query.pageSize,
        keyword: query.keyword || undefined,
        type: query.type,
        groupName: query.groupName,
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

  /** 筛选条件变化后回到第一页重查 */
  async function search(): Promise<void> {
    query.page = 1
    await loadMaterials()
  }

  async function resetAndReload(): Promise<void> {
    Object.assign(query, createDefaultQuery())
    await Promise.all([loadMaterials(), loadGroups()])
  }

  async function loadGroups(): Promise<void> {
    try {
      groups.value = await materialApi.fetchMaterialGroups()
    } catch {
      groups.value = []
    }
  }

  /**
   * 本地增减总数。
   *
   * 选秀成功 / 删除作品后先用它把角标改对，用户立刻看到数字变化，
   * 再由调用方异步 loadTotal 跟后端校准 —— 少一次等待，又不会长期偏离。
   */
  function bump(delta: number): void {
    if (!delta) return
    total.value = Math.max(0, total.value + delta)
  }

  /** 删除单件作品 */
  async function removeMaterial(id: string): Promise<void> {
    await materialApi.deleteMaterial(id)
    selectedIds.value = selectedIds.value.filter((item) => item !== id)
    bump(-1)
  }

  /** 批量删除作品 */
  async function removeMaterials(ids: string[]): Promise<void> {
    if (!ids.length) return
    await materialApi.batchDeleteMaterials(ids)
    selectedIds.value = []
    bump(-ids.length)
  }

  /** 删除后当前页可能空了一页，向前退一页再查 */
  async function reloadAfterRemove(): Promise<void> {
    const lastPage = Math.max(1, Math.ceil(total.value / (query.pageSize || 24)))
    if (query.page > lastPage) {
      query.page = lastPage
    }
    await loadMaterials()
  }

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

  const allSelected = computed(
    () => materials.value.length > 0 && selectedIds.value.length === materials.value.length,
  )

  function clearSelection(): void {
    selectedIds.value = []
  }

  /** 退出登录时清空，避免下一个账号看到上一个人的作品数 */
  function reset(): void {
    materials.value = []
    groups.value = []
    selectedIds.value = []
    total.value = 0
    Object.assign(query, createDefaultQuery())
  }

  return {
    materials,
    total,
    loading,
    counting,
    query,
    groups,
    selectedIds,
    isEmpty,
    allSelected,
    totalPages,

    loadTotal,
    loadMaterials,
    loadGroups,
    search,
    resetAndReload,
    bump,
    removeMaterial,
    removeMaterials,
    reloadAfterRemove,
    isSelected,
    toggleSelect,
    toggleSelectAll,
    clearSelection,
    reset,
  }
})
