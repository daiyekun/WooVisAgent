import type { RouteRecordRaw } from 'vue-router'

import AppLayout from '@/components/layout/AppLayout.vue'

/**
 * 业务路由
 * meta.title 会用于顶栏标题、侧边栏菜单和浏览器标题
 * meta.desc  顶栏标题下方的辅助说明
 * meta.icon  对应 @/components/common/AppIcon.vue 里的图标名
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
    redirect: '/tasks',
    meta: { requiresAuth: true },
    children: [
      {
        path: 'tasks',
        name: 'TaskList',
        component: () => import('@/views/task/TaskListView.vue'),
        meta: {
          title: '任务列表',
          desc: '查看、筛选并追踪全部生成任务',
          icon: 'list',
          order: 1,
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
          order: 2,
        },
      },
      {
        path: 'materials',
        name: 'MaterialLibrary',
        component: () => import('@/views/material/MaterialLibraryView.vue'),
        meta: {
          title: '素材库',
          desc: '按图片与视频分类检索已生成的产物',
          icon: 'images',
          order: 3,
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
          order: 4,
        },
      },
    ],
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'NotFound',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: '页面不存在', hidden: true },
  },
]

/** 侧边栏菜单，直接由路由派生，避免两处维护 */
export const menuRoutes = (routes.find((route) => route.path === '/')?.children ?? []).filter(
  (route) => !route.meta?.hidden,
)
