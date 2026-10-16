import type { RouteRecordRaw } from 'vue-router'

import AppLayout from '@/components/layout/AppLayout.vue'

/**
 * 业务路由
 * meta.title 会用于顶栏标题、侧边栏菜单和浏览器标题
 * meta.desc  顶栏标题下方的辅助说明
 * meta.icon  对应 @/components/common/AppIcon.vue 里的图标名
 * meta.chat  该页自带顶栏与输入区，外壳不再包一层 .scroll/.pad
 * meta.hidden 不在侧边栏出现（仍可通过 URL 或站内跳转访问）
 */
export const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'Login',
    component: () => import('@/views/auth/LoginView.vue'),
    meta: { title: '登录', hidden: true, guestOnly: true },
  },
  {
    path: '/register',
    name: 'Register',
    component: () => import('@/views/auth/RegisterView.vue'),
    meta: { title: '注册', hidden: true, guestOnly: true },
  },
  {
    path: '/',
    component: AppLayout,
    /**
     * 登录后的落地页是「工作台」，不是对话页。
     * 对话窗口只在点了「新建创作」（或从左侧菜单进「对话创作」）之后才出现。
     */
    redirect: '/workspace',
    meta: { requiresAuth: true },
    children: [
      {
        path: 'workspace',
        name: 'Workspace',
        component: () => import('@/views/workspace/WorkspaceView.vue'),
        meta: {
          title: '工作台',
          desc: '一个入口，开始你的图片与视频创作',
          icon: 'home',
          order: 0,
        },
      },
      {
        path: 'chat',
        name: 'Chat',
        component: () => import('@/views/chat/ChatView.vue'),
        meta: {
          title: '对话创作',
          desc: '一句话描述，图片与视频共用同一条对话流',
          icon: 'chat',
          chat: true,
          order: 1,
        },
      },
      {
        path: 'works',
        name: 'Works',
        component: () => import('@/views/material/MaterialLibraryView.vue'),
        meta: {
          title: '我的作品',
          desc: '按图片与视频分类检索已生成的产物',
          icon: 'grid',
          order: 2,
        },
      },
      {
        path: 'models',
        name: 'ModelConfig',
        component: () => import('@/views/model/ModelConfigView.vue'),
        meta: {
          title: '模型配置',
          desc: '接入你自己的模型服务、密钥与默认参数',
          icon: 'sliders',
          order: 3,
        },
      },
      {
        path: 'tasks',
        name: 'TaskList',
        component: () => import('@/views/task/TaskListView.vue'),
        meta: {
          title: '任务列表',
          desc: '查看、筛选并追踪全部生成任务',
          icon: 'list',
          hidden: true,
          order: 4,
        },
      },
      {
        path: 'tasks/create',
        name: 'TaskCreate',
        component: () => import('@/views/task/TaskCreateView.vue'),
        meta: {
          title: '新建任务',
          desc: '填写提示词与参数，提交图片或视频生成',
          icon: 'plus-circle',
          hidden: true,
          order: 5,
        },
      },
      // 旧路径兜底：素材库改名为「我的作品」后，避免历史收藏 / 外链 404
      { path: 'materials', redirect: '/works', meta: { hidden: true } },
    ],
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'NotFound',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: '页面不存在', hidden: true },
  },
]

/**
 * 侧边栏菜单，直接由路由派生，避免两处维护。
 *
 * 必须同时满足三个条件才算菜单项：没被 hidden、有 name、有 component。
 * 少判 name / component 会把纯重定向（如 materials → works）也当成菜单项，
 * 渲染出 `to={{ name: undefined }}` 的 router-link，router.resolve 直接抛错，
 * 结果是整个布局渲染中断、页面全白。
 */
export const menuRoutes = (routes.find((route) => route.path === '/')?.children ?? []).filter(
  (route) => !route.meta?.hidden && Boolean(route.name) && Boolean(route.component),
)
