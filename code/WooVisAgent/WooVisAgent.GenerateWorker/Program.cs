using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.EntityFrameworkCore;
using WooVisAgent.EventBus;
using WooVisAgent.GenerateWorker.Support;
using WooVisAgent.Infrastructure.Storage;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgentServices.ImageService;
using WooVisAgentServices.VideoService;

var builder = Host.CreateApplicationBuilder(args);

// 0. 后台进程没有 HTTP 上下文，先补一个"系统身份"。
//    用例程序集里的提交/查询/取消等处理器都依赖 ICurrentUser，
//    不注册会导致开发环境的服务校验直接抛错、Worker 起不来。
builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

// 1. Aspire 服务默认配置（OpenTelemetry、HealthChecks、服务发现）
builder.AddServiceDefaults();

// 2. 数据库上下文与仓储：Worker 需要按主键回查任务，并把生成结果与最终状态写回
builder.AddEfCore();

// 3. 文件存储：必须与 WooVisAgent.Api 使用同一实现、同一落盘目录，
//    产物才能在 Api 的 /uploads 静态托管下被前端页面直接加载
builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();

// 4. 图片生成服务（AGENT.md 中「调用图片生成服务进行生成」的那一层）
builder.AddImageService();

// 4.1 视频生成服务：消费 VideoGenerationRequested 并调用视频模型。
//     顺序在图片服务之后：视频侧复用了它注册的进度通知器等类型
builder.AddVideoService();

// 5. 僵尸任务清扫：把"状态停在 Running 却再也不动"的任务收敛成失败。
//    没有它，执行体一旦崩掉，任务就永远转圈——既不继续也不失败。
builder.Services.AddHostedService<StaleTaskReaper>();

// 6. RabbitMQ 事件总线（Wolverine）：只扫描当前程序集。
//    本程序集里只有一个处理器——ImageGenerationRequestedConsumer，
//    因此 Api 侧永远不会消费到生成任务消息，从结构上杜绝「双重消费」。
builder.AddEventBus(EventBusConstants.GenerateWorkerQueue, typeof(Program).Assembly);

var host = builder.Build();

// 开发环境兜底建表。通常 Api 已经建好，这里失败不影响启动，
// 所以只记警告，不让 Worker 因为一次并发建表竞争而崩掉。
if (!bool.TryParse(builder.Configuration["Database:AutoInitialize"], out var autoInitialize) || autoInitialize)
{
    try
    {
        await host.Services.InitializeDatabaseAsync();
    }
    catch (Exception exception)
    {
        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Startup")
            .LogWarning(exception, "数据库初始化未完成，将沿用 Api 建好的表结构");
    }
}

await host.RunAsync();
