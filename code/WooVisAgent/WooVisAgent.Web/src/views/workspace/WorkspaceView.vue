<script setup lang="ts">
/**
 * ============================================================================
 * 工作台 · 登录后的落地页
 * ============================================================================
 *
 * 定位：右侧的默认内容。对话窗口不在这里打开——必须点「新建创作」（或左侧菜单
 * 的「对话创作」）才会切到对话页，避免一登录就撞进一条陌生的会话里。
 *
 * 版式沿用原型 docs/prot/app.html 的语言：卡片 + 起始入口 + 缩略图网格，
 * 与外壳的左侧菜单组成「工作台」整体。
 */
import { computed } from 'vue'
import { useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import { TASK_STATUS_META, TASK_TYPE_META } from '@/constants'
import { useAuthStore } from '@/stores/authStore'
import { useModelStore } from '@/stores/modelStore'
import { useTaskStore } from '@/stores/taskStore'
import type { GenerationTask, TaskAsset, TaskStatus } from '@/types'
import { formatDateTime } from '@/utils'

const router = useRouter()
const authStore = useAuthStore()
const modelStore = useModelStore()
const taskStore = useTaskStore()

/** ==================== 问候语 ==================== */

const greeting = computed(() => {
  const hour = new Date().getHours()
  if (hour < 6) return '夜深了'
  if (hour < 12) return '早上好'
  if (hour < 14) return '中午好'
  if (hour < 18) return '下午好'
  return '晚上好'
})

/** ==================== 概览 ==================== */

/** 已完成的任务（作品来源） */
const finishedTasks = computed(() =>
  taskStore.tasks.filter((task) => task.status === 'succeeded'),
)

/** 作品 = 已完成任务的产物摊平，一条产物算一件作品 */
const works = computed(() =>
  finishedTasks.value.flatMap((task) =>
    (task.outputs ?? []).map((asset) => ({ id: `${task.id}-${asset.id}`, task, asset })),
  ),
)

/** 概览指标；to 决定点击后去哪一页 */
const stats = computed(() => [
  {
    key: 'works',
    label: '我的作品',
    value: works.value.length,
    icon: 'grid',
    hint: '已完成任务的产物',
    to: 'Works' as const,
  },
  {
    key: 'tasks',
    label: '生成任务',
    value: taskStore.total,
    icon: 'list',
    hint: '累计提交过的任务',
    to: 'TaskList' as const,
  },
  {
    key: 'running',
    label: '进行中',
    value: taskStore.runningCount,
    icon: 'clock',
    hint: '排队或生成中的任务',
    to: 'TaskList' as const,
  },
  {
    key: 'models',
    label: '可用模型',
    value: modelStore.enabledModels.length,
    icon: 'sliders',
    hint: '已启用且可调用的模型',
    to: 'ModelConfig' as const,
  },
])

/** 最近任务：只看前 6 条，够用来做「上次做到哪」的提示 */
const recentTasks = computed(() => taskStore.tasks.slice(0, 6))

/** 最近作品：取最新 8 件 */
const recentWorks = computed(() => works.value.slice(0, 8))

const STATUS_TAG_CLASS: Record<TaskStatus, string> = {
  pending: 'tag-dim',
  queued: 'tag-cyan',
  running: 'tag-warn',
  succeeded: 'tag-ok',
  failed: 'tag-err',
  canceled: 'tag-dim',
}

function statusTagClass(status: TaskStatus): string {
  return STATUS_TAG_CLASS[status] ?? 'tag-dim'
}

/** ==================== 跳转 ==================== */

/**
 * 新建创作：带 mode 进对话页，对话页会清掉上一轮会话并切到对应模式。
 * 时间戳只为了让 query 每次都不同，从而必然触发对话页的 watch。
 */
function startCreate(mode: 'image' | 'video'): void {
  void router.push({ name: 'Chat', query: { new: String(Date.now()), mode } })
}

function openWorks(): void {
  void router.push({ name: 'Works' })
}

function refresh(): void {
  void taskStore.loadTasks({ page: 1, pageSize: 8 })
  void modelStore.loadModels()
}

/** ==================== 缩略图 ==================== */

const WORK_GRADIENTS = [
  'linear-gradient(150deg,#0F2E22,#047857)',
  'linear-gradient(150deg,#0b2a3a,#0e7490)',
  'linear-gradient(150deg,#241b4d,#5b21b6)',
  'linear-gradient(150deg,#3a1d0b,#c2410c)',
]

/** 静态期没有真实图片时的占位底色，按 id 稳定取一个 */
function gradientOf(key: string): string {
  let sum = 0
  for (let i = 0; i < key.length; i += 1) sum += key.charCodeAt(i)
  return WORK_GRADIENTS[sum % WORK_GRADIENTS.length]!
}

function thumbUrl(asset: TaskAsset): string {
  return asset.thumbnailUrl || asset.url || ''
}

/** 视频时长 00:04 这种展示 */
function durationText(asset: TaskAsset): string {
  const total = Math.max(0, Math.round(asset.duration ?? 0))
  const mm = String(Math.floor(total / 60)).padStart(2, '0')
  const ss = String(total % 60).padStart(2, '0')
  return `${mm}:${ss}`
}

function taskTitle(task: GenerationTask): string {
  return task.name || task.prompt || '未命名任务'
}
</script>

<template>
  <section class="ws">
    <!-- ==================== 标题条 ==================== -->
    <div class="page-head">
      <h1>
        工作台
        <small>{{ greeting }}，{{ authStore.displayName }} · 今天想做点什么？</small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <button class="btn btn-ghost" type="button" :disabled="taskStore.loading" @click="refresh">
          <app-icon name="refresh" :size="15" />
          刷新
        </button>
      </div>
    </div>

    <!-- ==================== 新建创作入口 ==================== -->
    <div class="ws-hero card">
      <div class="ws-hero-main">
        <span class="ws-eyebrow">
          <span class="dot" style="background: var(--brand)" />
          图片 · 视频 · 一个工作台
        </span>

        <h2>把想法说出来<br /><em>剩下的交给模型</em></h2>

        <p>
          在对话里直接描述你要的画面或镜头，平台调用你配置的模型完成生成。 所有产物自动归档到
          「我的作品」，随时回看与复用。
        </p>

        <div class="ws-actions">
          <button class="btn btn-primary" type="button" @click="startCreate('image')">
            <app-icon name="image" :size="16" />
            新建图片创作
          </button>
          <button class="btn btn-ghost" type="button" @click="startCreate('video')">
            <app-icon name="video" :size="16" />
            新建视频创作
          </button>
          <button class="btn btn-ghost" type="button" @click="openWorks">
            <app-icon name="grid" :size="16" />
            我的作品
          </button>
        </div>
      </div>

      <div class="ws-hero-art" aria-hidden="true">
        <span class="tile t1">🎨</span>
        <span class="tile t2">🎬</span>
        <span class="tile t3">🥤</span>
      </div>
    </div>

    <!-- ==================== 概览 ==================== -->
    <div class="ws-stats">
      <button
        v-for="item in stats"
        :key="item.key"
        class="ws-stat card"
        type="button"
        :title="`去${item.label}`"
        @click="router.push({ name: item.to })"
      >
        <span class="ic"><app-icon :name="item.icon" :size="17" /></span>
        <b>{{ item.value }}</b>
        <span class="lb">{{ item.label }}</span>
        <small>{{ item.hint }}</small>
      </button>
    </div>

    <!-- ==================== 最近作品 ==================== -->
    <div class="card card-pad ws-block">
      <div class="card-head">
        <div>
          <h3><app-icon name="grid" :size="15" />最近作品</h3>
          <div class="sub">已完成任务的产物，点缩略图去作品库继续处理</div>
        </div>

        <button v-if="works.length" class="btn btn-ghost btn-sm" type="button" @click="openWorks">
          查看全部
          <app-icon name="chevron-right" :size="14" />
        </button>
      </div>

      <div v-if="recentWorks.length" class="ws-grid">
        <button
          v-for="item in recentWorks"
          :key="item.id"
          class="ws-thumb"
          type="button"
          :title="taskTitle(item.task)"
          @click="openWorks"
        >
          <img v-if="thumbUrl(item.asset)" :src="thumbUrl(item.asset)" :alt="taskTitle(item.task)" />
          <span v-else class="glyph" :style="{ background: gradientOf(item.id) }">
            {{ item.asset.type === 'video' ? '🎬' : '🎨' }}
          </span>

          <span v-if="item.asset.type === 'video'" class="ws-play">
            <i />
          </span>
          <span v-if="item.asset.type === 'video'" class="ws-dur">
            {{ durationText(item.asset) }}
          </span>
          <span class="ws-kind" :class="item.asset.type === 'video' ? 'is-video' : 'is-image'">
            {{ TASK_TYPE_META[item.task.type].label }}
          </span>
        </button>
      </div>

      <EmptyState
        v-else
        icon="grid"
        title="还没有作品"
        description="点上面的「新建图片创作」或「新建视频创作」，第一条对话就能出图 / 出片。"
      >
        <button class="btn btn-primary btn-sm" type="button" @click="startCreate('image')">
          <app-icon name="plus" :size="15" />
          新建创作
        </button>
      </EmptyState>
    </div>

    <!-- ==================== 最近任务 ==================== -->
    <div class="card card-pad ws-block">
      <div class="card-head">
        <div>
          <h3><app-icon name="list" :size="15" />最近任务</h3>
          <div class="sub">最近提交的生成任务与实时状态</div>
        </div>

        <router-link class="btn btn-ghost btn-sm" :to="{ name: 'TaskList' }">
          全部任务
          <app-icon name="chevron-right" :size="14" />
        </router-link>
      </div>

      <div v-if="recentTasks.length" class="ws-rows">
        <button
          v-for="task in recentTasks"
          :key="task.id"
          class="ws-row"
          type="button"
          @click="router.push({ name: 'TaskList' })"
        >
          <span class="ic">
            <app-icon :name="TASK_TYPE_META[task.type].icon" :size="14" />
          </span>

          <span class="tt">
            <b>{{ taskTitle(task) }}</b>
            <small>{{ task.modelName || task.modelId || '未指定模型' }}</small>
          </span>

          <span class="tag" :class="statusTagClass(task.status)">
            {{ TASK_STATUS_META[task.status].label }}
          </span>

          <span class="tm">{{ formatDateTime(task.createdAt, 'MM-DD HH:mm') }}</span>
        </button>
      </div>

      <EmptyState
        v-else
        icon="inbox"
        title="还没有生成记录"
        description="工作台会在这里显示你最近的任务进度与状态。"
      />
    </div>
  </section>
</template>

<style scoped>
.ws {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

/* ---------- 新建创作入口 ---------- */
.ws-hero {
  position: relative;
  overflow: hidden;
  display: flex;
  align-items: center;
  gap: 24px;
  padding: 26px 28px;
  background:
    radial-gradient(120% 140% at 88% 12%, rgba(124, 92, 252, 0.16), transparent 58%),
    radial-gradient(120% 140% at 8% 96%, rgba(18, 181, 229, 0.14), transparent 60%),
    var(--card);
}

.ws-hero-main {
  flex: 1;
  min-width: 0;
}

.ws-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 11.5px;
  font-weight: 700;
  letter-spacing: 0.04em;
  color: var(--sub);
  text-transform: uppercase;
}

.ws-hero h2 {
  font-size: 24px;
  font-weight: 900;
  letter-spacing: -0.6px;
  line-height: 1.28;
  margin-top: 12px;
}

.ws-hero h2 em {
  font-style: normal;
  background: var(--grad);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.ws-hero p {
  font-size: 13px;
  color: var(--sub);
  margin-top: 10px;
  max-width: 620px;
  line-height: 1.7;
}

.ws-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-top: 18px;
}

/* 装饰缩略图，小屏直接隐藏，不影响信息密度 */
.ws-hero-art {
  display: flex;
  gap: 10px;
  flex-shrink: 0;
}

.ws-hero-art .tile {
  width: 74px;
  height: 74px;
  border-radius: var(--r-lg);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 26px;
  border: 1px solid var(--line);
}

.ws-hero-art .t1 {
  background: linear-gradient(150deg, #0b2a3a, #0e7490);
}

.ws-hero-art .t2 {
  background: linear-gradient(150deg, #241b4d, #5b21b6);
  margin-top: 16px;
}

.ws-hero-art .t3 {
  background: linear-gradient(150deg, #3a1d0b, #c2410c);
}

/* ---------- 概览指标 ---------- */
.ws-stats {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}

.ws-stat {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 16px 18px;
  text-align: left;
  font-family: inherit;
  cursor: pointer;
  transition: var(--t);
}

.ws-stat:hover {
  border-color: rgba(18, 181, 229, 0.4);
  transform: translateY(-2px);
}

.ws-stat .ic {
  width: 30px;
  height: 30px;
  border-radius: 9px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--brand-dim);
  color: var(--brand);
  margin-bottom: 8px;
}

.ws-stat b {
  font-size: 22px;
  font-weight: 900;
  letter-spacing: -0.5px;
  color: var(--text);
}

.ws-stat .lb {
  font-size: 12.5px;
  font-weight: 700;
  color: var(--sub);
}

.ws-stat small {
  font-size: 11.5px;
  color: var(--dim);
}

/* ---------- 板块 ---------- */
.ws-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 12px;
}

.ws-thumb {
  position: relative;
  border-radius: var(--r);
  overflow: hidden;
  border: 1px solid var(--line-2);
  aspect-ratio: 1 / 1;
  background: var(--elev);
  cursor: pointer;
  transition: var(--t);
  padding: 0;
  font-family: inherit;
}

.ws-thumb:hover {
  border-color: rgba(18, 181, 229, 0.45);
  transform: translateY(-2px);
}

.ws-thumb img,
.ws-thumb .glyph {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  object-fit: cover;
  font-size: 30px;
}

.ws-play {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}

.ws-play i {
  width: 38px;
  height: 38px;
  border-radius: 50%;
  background: rgba(6, 18, 26, 0.55);
  border: 1px solid rgba(255, 255, 255, 0.4);
  backdrop-filter: blur(3px);
}

.ws-play i::after {
  content: '';
  display: block;
  margin: 12px 0 0 15px;
  border-style: solid;
  border-width: 7px 0 7px 11px;
  border-color: transparent transparent transparent #fff;
}

.ws-dur {
  position: absolute;
  right: 8px;
  bottom: 8px;
  font-size: 10.5px;
  font-weight: 700;
  color: #fff;
  background: rgba(6, 18, 26, 0.62);
  border-radius: 6px;
  padding: 2px 6px;
}

.ws-kind {
  position: absolute;
  left: 8px;
  top: 8px;
  font-size: 10.5px;
  font-weight: 700;
  border-radius: 999px;
  padding: 2px 8px;
  color: #fff;
}

.ws-kind.is-image {
  background: rgba(8, 145, 178, 0.86);
}

.ws-kind.is-video {
  background: rgba(124, 92, 252, 0.86);
}

/* ---------- 最近任务 ---------- */
.ws-rows {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ws-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 9px 10px;
  border-radius: var(--r);
  background: transparent;
  border: 1px solid transparent;
  cursor: pointer;
  transition: var(--t);
  text-align: left;
  font-family: inherit;
}

.ws-row:hover {
  background: var(--card-2);
  border-color: var(--line-2);
}

.ws-row .ic {
  width: 28px;
  height: 28px;
  border-radius: 9px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--elev);
  color: var(--sub);
}

.ws-row .tt {
  flex: 1;
  min-width: 0;
}

.ws-row .tt b {
  display: block;
  font-size: 13px;
  font-weight: 700;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-row .tt small {
  display: block;
  font-size: 11.5px;
  color: var(--dim);
  margin-top: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-row .tm {
  font-size: 11.5px;
  color: var(--dim);
  flex-shrink: 0;
}

/* ---------- 窄屏 ---------- */
@media (max-width: 1100px) {
  .ws-stats {
    grid-template-columns: repeat(2, 1fr);
  }

  .ws-hero-art {
    display: none;
  }
}

@media (max-width: 640px) {
  .ws-hero {
    padding: 20px;
  }

  .ws-hero h2 {
    font-size: 20px;
  }

  .ws-actions .btn {
    flex: 1;
    justify-content: center;
  }
}
</style>
