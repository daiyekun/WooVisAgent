/**
 * ============================================================================
 * 对话创作 · 静态示例数据
 * ============================================================================
 *
 * 这一批数据 1:1 照抄原型 docs/prot/app.html，仅用于把版式跑通。
 * 真实数据接入时：
 *   1. messages 改为由「会话 + 任务」推导（图片/视频共用同一会话流）；
 *   2. thumb 补上 url 字段即可自动切换成真实图片 / 视频封面（模板已做分支）；
 *   3. modelOptions 改为读 modelStore，按 image / video 分类过滤。
 */

export type ChatMode = 'image' | 'video'

/** 缩略图占位：有 url 渲染真图，没有则退化成渐变块 + 字符占位 */
export interface MockThumb {
  id: string
  glyph: string
  bg: string
  url?: string
  wide?: boolean
  duration?: string
}

/** 一次生成的结果卡 */
export interface MockGenCard {
  kind: ChatMode
  title: string
  model: string
  meta: string
  thumbs: MockThumb[]
}

export interface MockMessage {
  id: string
  role: 'user' | 'assistant'
  time: string
  text: string
  attachments?: MockThumb[]
  card?: MockGenCard
}

const SKIN_CARE_GLYPH = { glyph: '🧴', bg: 'linear-gradient(150deg,#0F2E22,#047857)' }
const PLANT_GLYPH = { glyph: '🌿', bg: 'linear-gradient(150deg,#122634,#0E7490)' }
const WATER_GLYPH = { glyph: '🌊', bg: 'linear-gradient(150deg,#0B2A3A,#0E7490)' }

export const initialMessages: MockMessage[] = [
  {
    id: 'm-1',
    role: 'user',
    time: '刚刚',
    text: '做一张极简日式风格的护肤精华海报，大量留白，柔和自然光，瓶身居中，右下角留出品牌文案位置。',
    attachments: [
      { id: 'a-1', ...SKIN_CARE_GLYPH },
      { id: 'a-2', ...PLANT_GLYPH },
    ],
  },
  {
    id: 'm-2',
    role: 'assistant',
    time: '刚刚',
    text: '已按你的参考图生成 2 张方案，均为 1024×1024。左图保留更多留白，适合做主视觉；右图把植物元素前置，更像情景图。可以告诉我要往哪个方向继续调整。',
    card: {
      kind: 'image',
      title: '图片生成 · 2 张',
      model: 'GPT-Image-1',
      meta: '1024×1024 · 用时 6.2s',
      thumbs: [
        { id: 't-1', ...SKIN_CARE_GLYPH },
        { id: 't-2', ...PLANT_GLYPH },
      ],
    },
  },
  {
    id: 'm-3',
    role: 'user',
    time: '2 分钟前',
    text: '接着做一条 4 秒的短视频：瓶身缓慢旋转，背景水波微动，光斑从左向右扫过，节奏舒缓。',
  },
  {
    id: 'm-4',
    role: 'assistant',
    time: '2 分钟前',
    text: '视频已生成完成，以左图作为首帧保持一致，运镜按“缓推 + 光斑横扫”处理。',
    card: {
      kind: 'video',
      title: '视频生成 · 1 条',
      model: 'Sora-2',
      meta: '1280×720 · 4s · 用时 48s',
      thumbs: [{ id: 't-3', wide: true, duration: '00:04', ...WATER_GLYPH }],
    },
  },
]

/**
 * 首次进入对话页是否铺一份示例会话。
 *
 * true  —— 直接看到图片卡与视频卡的真实版式（静态页阶段用这个）；
 * false —— 落「欢迎屏 + 起始卡片」的空态。接入真实接口后应改为 false，
 *          会话列表交给后端。
 */
export const SHOW_DEMO_SESSION = true

/** 空会话时的起始卡片 */export const starterCards = [
  {
    icon: 'image',
    tone: 'ic-a',
    title: '做一张产品主视觉',
    desc: '极简留白，柔和自然光',
    prompt: '做一张极简风格的产品主视觉，大量留白，柔和自然光，主体居中，右下角留出品牌文案位置。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'video',
    tone: 'ic-b',
    title: '拍一条商品短视频',
    desc: '慢镜头推近，光斑横扫',
    prompt: '做一条 4 秒短视频：主体缓慢旋转，背景微动，光斑从左向右扫过，节奏舒缓，慢镜头推近。',
    mode: 'video' as ChatMode,
  },
  {
    icon: 'sparkles',
    tone: 'ic-c',
    title: '改一版现有画面',
    desc: '保留构图，只换氛围光',
    prompt: '保留当前构图，把氛围光换成傍晚暖调，增加轻微胶片颗粒感。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'grid',
    tone: 'ic-d',
    title: '一次出四张方案',
    desc: '同一提示词横向对比',
    prompt: '同一个提示词出 4 张不同构图方案，方便我横向对比挑选。',
    mode: 'image' as ChatMode,
  },
]

/** 输入区可选项（静态版：点击循环切换） */
export const composerOptions = {
  image: {
    /** 比例 */
    ratio: ['1:1', '16:9', '9:16', '3:4'],
    /** 尺寸档位，拼在比例下方显示 */
    size: ['1024×1024', '1280×720', '720×1280', '896×1152'],
    models: ['GPT-Image-1', 'Qwen-Image-3.0', 'Flux-1.1-Pro'],
  },
  video: {
    resolution: ['720p', '1080p'],
    duration: ['4s', '6s', '8s'],
    models: ['Sora-2', 'Wan-2.5', 'Kling-2.1'],
  },
}

/** 生成中的阶段文案（静态演示用） */
export const generatingStages: Record<ChatMode, string[]> = {
  image: ['正在解析提示词', '正在构图', '正在细化细节', '正在合成输出'],
  video: ['排队中', '正在解码首帧', '正在生成关键帧', '正在合成输出'],
}

/** 空会话标题 */
export const NEW_CHAT_TITLE = '新建创作'
