# WooVisAgent.Web

WooVisAgent 前端控制台，技术栈 **Vue3 + Vite + TypeScript + Pinia + NaiveUI + TailwindCSS**，
通过 **HTTP + SignalR** 与后端 `WooVisAgent.Api` 对接。

## 目录结构

```
WooVisAgent.Web/
├── public/
│   └── favicon.svg
├── src/
│   ├── api/                  # 接口封装
│   │   ├── http.ts           # axios 实例、鉴权头、错误归一化
│   │   ├── types.ts          # ApiError / ProblemDetails / 上传结果
│   │   ├── auth.ts           # 注册 / 登录 / 邮箱验证码 / 当前用户
│   │   ├── image.ts          # 图片生成
│   │   ├── video.ts          # 视频生成
│   │   ├── task.ts           # 任务列表 / 详情 / 取消 / 重试 / 删除
│   │   ├── model.ts          # 模型配置、连通性测试
│   │   ├── material.ts       # 素材库
│   │   └── index.ts          # 统一出口（authApi / taskApi / modelApi ...）
│   ├── assets/styles/        # 全局样式与 NaiveUI 主题变量
│   ├── components/           # 公共组件
│   │   ├── TaskCard.vue
│   │   ├── ImagePreview.vue
│   │   ├── VideoPlayer.vue
│   │   ├── ModelSettingForm.vue
│   │   ├── common/           # AppIcon、EmptyState
│   │   └── layout/           # AppLayout、AppHeader、AppSidebar
│   ├── constants/            # 状态/类型字典、分页等常量
│   ├── hooks/useSignalR.ts   # SignalR 连接与任务进度订阅
│   ├── router/               # 路由、菜单与登录守卫
│   ├── stores/               # Pinia：appStore、authStore、taskStore、modelStore
│   ├── types/                # 业务类型定义
│   ├── utils/                # 时间格式化、文件大小转换、消息反馈
│   ├── views/                # 页面
│   │   ├── auth/             # 登录、注册
│   │   ├── task/             # 任务列表、新建任务
│   │   ├── material/         # 素材库
│   │   ├── model/            # 模型配置
│   │   └── NotFoundView.vue
│   ├── App.vue
│   └── main.ts
├── .env.development
├── .env.production
├── env.d.ts
├── index.html
├── tsconfig.json
├── vite.config.ts
└── package.json
```

## 本地开发

```bash
npm install
npm run dev          # http://localhost:5199
```

### 在 Aspire 中联调（推荐）

直接启动 `WooVisAgent.AppHost` 即可，`vite.config.ts` 会自动适配：

- 端口：AppHost 的 `AddViteApp(...).WithEndpoint("http", e => e.Port = 5199)` 通过 `PORT` 环境变量传入，
  前端以 `strictPort` 方式监听，避免端口漂移导致代理指向错误。
- 后端地址：AppHost 的 `WithReference(httpapi)` 注入 `services__woovisagent-api__http__0`，
  前端把它作为 `/api`、`/hubs` 的代理目标，因此**不需要配置 CORS**。

单独执行 `npm run dev`（不经 AppHost）时，用 `.env.development` 里的 `VITE_PROXY_TARGET` 指定后端地址。

### 不启动 Docker 的联调方式

本机没有 PostgreSQL 时，可让后端改用 EF InMemory 存储（重启即清空，仅用于联调）：

```bash
cd WooVisAgent.Api
Database__Provider=InMemory dotnet run --urls http://127.0.0.1:5210
```

此时注册 / 登录可直接跑通：先 `POST /api/auth/register`，再用返回的 Token 访问受保护接口。

## 常用脚本

| 命令 | 说明 |
| --- | --- |
| `npm run dev` | 启动开发服务器 |
| `npm run build` | 类型检查 + 生产构建 |
| `npm run build:only` | 跳过类型检查直接构建 |
| `npm run type-check` | 仅执行 `vue-tsc` 类型检查 |
| `npm run preview` | 预览构建产物 |

## 对接说明

- **REST 路径**：`/api/tasks`、`/api/image/generations`、`/api/video/generations`、`/api/models`、`/api/materials`。
  后端路由确定后，只需改 `src/api/*.ts` 里的 `BASE_URL` 常量。
- **SignalR Hub**：默认 `/hubs/tasks`，事件名为 `TaskCreated`、`TaskStatusChanged`、`TaskProgress`、
  `TaskCompleted`、`TaskFailed`，见 `src/hooks/useSignalR.ts`。连接失败不会阻塞页面，
  可通过 `appStore.autoRefreshInterval` 打开定时轮询兜底。
- **鉴权**：登录成功由 `authStore` 调用 `setAuthToken(token)` 写入 JWT，`http.ts` 自动带上
  `Authorization` 头，401 时清理本地会话；路由守卫见 `src/router/index.ts`，
  业务路由统一 `meta.requiresAuth`，`/login`、`/register` 为 `meta.guestOnly`。
- **身份接口**（`src/api/auth.ts`，后端实现见 `WooVisAgent.Api/Controllers/AuthController.cs`）：

  | 方法 | 路径 | 说明 |
  | --- | --- | --- |
  | POST | `/api/auth/register` | 邮箱 + 密码（+ 可选验证码），成功即返回 Token |
  | POST | `/api/auth/login` | `account` 支持邮箱或用户名 |
  | POST | `/api/auth/email-code` | 发送验证码；未接入邮件通道时开发环境直接回传 |
  | GET | `/api/auth/me` | 读取当前用户资料，刷新页面后恢复会话 |
- **主题**：NaiveUI 主题变量在 `src/assets/styles/naive-theme.ts`，
  Tailwind 主色在 `src/assets/styles/index.css` 的 `@theme` 中，两处保持一致。
  Tailwind 只引入了 `theme` + `utilities` 两层，跳过了 preflight，避免覆盖 NaiveUI 的样式重置。
