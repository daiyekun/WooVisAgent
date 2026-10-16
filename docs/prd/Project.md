# 项目结构介绍

~~~
WooVisAgent.Api：API接口，对外提供

WooVisAgent.AppHost：host

WooVisAgent.ServiceDefaults:默认值

WooVisAgent.EntityFrameworkCore：EF ORM工具封装类

WooVisAgent.EventBus：事件对象项目

WooVisAgent.Infrastructure：一些实现工具类，比如存储实现，

WooVisAgent.Services.Common：服务工具，公共类

WooVisAgentServices.IdentityService：用户token颁发和校验类库

WooVisAgent.SharedLib：通用公共类
WooVisAgent.Web:这个文件夹主要防止前端页面，所有前端程序都防止到这里面
~~~

# 技术说明

前端技术： Vue3 + Vite + TS + Pinia + NaiveUI + TailwindCSS  ， 做轻量 Web 面板，通过 HTTP+SingalR(.NET  websocket )对接 

~~~
WooVisAgent.Web/
├── src/
│   ├── api/            # API请求封装（image generation、video、task、model config）
│   ├── components/     # 公共组件：TaskCard、ImagePreview、VideoPlayer、ModelSettingForm
│   ├── views/          # 页面：任务列表、新建任务、素材库、模型配置
│   ├── stores/         # Pinia状态：taskStore、modelStore
│   ├── router/         # 路由
│   ├── utils/          # 工具：时间格式化、文件大小转换
│   ├── App.vue
│   └── main.ts
├── vite.config.ts
├── tsconfig.json
└── package.json

~~~

