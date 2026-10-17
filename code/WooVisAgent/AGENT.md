# 引用

~~~
简要需求：D:\WooProject\AIVidio\docs\prd\PRD.md
项目文件简要：D:\WooProject\AIVidio\docs\prd\Project.md
原型文件夹：D:\WooProject\AIVidio\docs\prot
后台工作服务：WooVisAgent.GenerateWorker    ---当前提交任务以后，发送事件信息到Rabbitmq ,当前服务拿到Rabbitmq信息，调用图片生成和视频生成服务进行生成。生成完改任务状态。前端界面拿到完成状态进行刷新显示
图片服务：WooVisAgentServices.ImageService
视频服务：WooVisAgentServices.VideoService    ---与图片同构的异步链路（提交→RabbitMQ→Worker生成→SignalR推送）。交付协议走「预设+可配置模板」：内置 openai-videos / dashscope-video / dashscope-legacy-video / siliconflow-video 四个预设，未覆盖的厂商在模型配置的「交付模板」里改路径与字段映射即可接入，不改代码。
模型提供商服务：WooVisAgentServices.ModelService --比如设置模型功能
~~~

## 视频生成的几个关键约定

- **视频接口没有事实标准**（不同于图片有 OpenAI Images）：提交路径、轮询 HTTP 方法、任务号字段、产物形态（URL / 二进制 content / base64）各家全不一样，因此这些差异被做成 `SharedLib/Delivery/VideoDeliverySpec.cs` 里的**配置数据**而非厂商硬编码类。新接厂商优先改模型配置，实在表达不了的才加预设。
- **预设推断必须以主机名为准**：模型名会跨平台重名（硅基流动托管的模型也叫 `Wan-AI/Wan2.2-T2V-A14B`，含 "wan"）。`VideoDeliveryResolver.InferPreset` 里硅基流动的判断排在百炼之前，就是为了防止误判。
- **endpoint 与相对路径容易带重复前缀**：`VideoDeliverySpec.Combine` 会消除 `.../api/v1/api/v1/...`，改动这个方法前务必先看它的注释。
- **分辨率档位看短边**：竖屏 `720x1280` 属于 720P，按高度判断会误成 1080P。
- **ffmpeg 是可选依赖**：缺失时视频照常生成成功，只是没有封面与时长信息。路径在 `WooVisAgent.GenerateWorker/appsettings.json` 的 `VideoMedia:FfmpegPath` 配置（也可设环境变量 `VideoMedia__FfmpegPath`），填完整路径或留空走 PATH 均可。启动时**真的执行一次 `-version`** 来判定可用，不要改回"看路径猜"。同理，`RunAsync` 里必须**先 `process.Start()` 再读 `StandardOutput`**——写反了会被上层 catch 吞掉，表现为"ffmpeg 明明装了却一直没封面"。
- **视频任务重试走 ImageService 的 `RetryGenerationTask`**（该用例类型无关），因此 `GenerationTaskDispatcher` 必须按 `Task.Type` 分派，否则视频重试会原地打转。
- **给已有表加必填字段必须配 `HasDefaultValue`**（在 `EntityTypeConfiguration` 里，不是 C# 属性初始值）。否则启动时自动补列会发出无 DEFAULT 的 `ADD COLUMN ... NOT NULL`，存量行直接触发 PostgreSQL 23502。`SchemaEvolutionTests` 会在编译期就抓到这个错。
- **补列只用 `ExecuteNonFormattedSqlAsync`**：`ExecuteSqlRawAsync` 内部走 `string.Format`，默认值 `'{}'` 会被当成格式占位符，报 `FormatException: Expected an ASCII digit`。查询侧同理，`SqlQueryRaw` 也有这个问题。
- **轮询上限 15 分钟**，HttpClient 超时 20 分钟，弹性管道总超时 5 分钟——三者刻意保持这个大小关系，让超时一定由业务逻辑给出可读中文。
~~~
