# WooVisAgent 项目长期备忘

## 架构要点（易忘、改错会出线上事故）

- 异步生成链路：Api 落库 → RabbitMQ（Wolverine，MIT 协议，替代商业授权的 MassTransit 9）→
  GenerateWorker 消费 → SignalR 推回前端。图片与视频共用同一套结构。
- **消息执行超时**：`WolverineOptions.DefaultExecutionTimeout` 默认只有 60 秒，
  本项目在 `WooVisAgent.EventBus/DependencyInjection.cs` 设为 30 分钟。
  任何耗时超过 1 分钟的任务（视频轮询 20 分钟）否则会被框架精准掐断并丢进死信队列。
- **时长相关的常量联动**：`VideoDeliveryFramework.MaxPollMinutes`（20）改动必须同步四处——
  `VideoGenerationRequestedConsumer.StaleRunningThreshold`、`StaleTaskReaper.VideoThreshold`、
  `EventBus.DependencyInjection.DefaultMessageExecutionTimeout`、前端"最长等待 N 分钟"文案。
- 数据库不走 EF Migration，靠启动时 `InitializeDatabaseAsync` 三段式自建/补列。
  给必填列加字段时**必须在 `ModelConfigConfiguration` 里配 `HasDefaultValue`**——
  C# 属性初始值不会进 EF 元数据，否则存量行立刻 23502，服务起不来。
- 项目里 `ExecuteSqlRawAsync` 走 `string.Format`，含 `{}` 的 SQL 要用 `ExecuteNonFormattedSqlAsync`。

## 约定与经验

- 时长/超时类断言**不要在测试里抄字面量**，从实际装配出的对象上读（如 `IHttpClientFactory`
  造出的 client 的 `Timeout`），否则改了注册处的值测试会永久失真（已踩过两次）。
- 新增测试后**把修复临时回退一次，确认它真的会变红**，避免写出"永远通过"的假测试。
- `dotnet test` 会被正在运行的服务锁 DLL。服务不停时用 `-c Release` 跑测试可绕开；
  只编译验证用 `dotnet build -t:Compile -nodeReuse:false`。
  不要用 `-p:BaseOutputPath` 改输出目录（会生成重复 AssemblyInfo 报 CS0579）。
- 用户手动重启 AppHost 后**旧进程不会自动退出**，会残留第二个 Worker 抢消费。
  改完代码重启前先确认旧进程已停干净，否则跑的可能是旧代码。
- 前端解构 store 必须用 `storeToRefs`；`const x = store.x` 对 state 会退化成快照
  （作品库筛选失效就是这么来的）。

## 环境

- Postgres / RabbitMQ 由 Aspire 随机分配端口，容器名 `postgres-b58fdf20` / `eventbus-b58fdf20`，
  口令 123456，库 `woovisagent`。表名 snake_case、列名 PascalCase。
  查询：`docker exec -e PGPASSWORD=123456 postgres-b58fdf20 psql -U postgres -d woovisagent -c '<sql>'`
- 当前视频模型：`wan3.0-video`，endpoint `https://maas.qianwenaiapi.com`（第三方网关，
  主机名不含 dashscope，**必须显式选"百炼 DashScope"预设**，否则会落到 legacy 的 size 写法）。
- 前端脚本用托管 npm：`& "C:\Users\user\.workbuddy\binaries\node\versions\22.22.2-6\npm.cmd" --prefix <Web目录> run build`
