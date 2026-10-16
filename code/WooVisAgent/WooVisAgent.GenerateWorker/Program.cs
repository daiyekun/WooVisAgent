using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.EntityFrameworkCore;
using WooVisAgent.EventBus;
using WooVisAgent.Infrastructure.Storage;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgentServices.ImageService;

var builder = Host.CreateApplicationBuilder(args);

// 1. Aspire 服务默认配置（OpenTelemetry、HealthChecks、服务发现）
builder.AddServiceDefaults();

// 2. 数据库上下文与仓储：Worker 需要按主键回查任务，并把生成结果与最终状态写回
builder.AddEfCore();

// 3. 文件存储：必须与 WooVisAgent.Api 使用同一实现、同一落盘目录，
//    产物才能在 Api 的 /uploads 静态托管下被前端页面直接加载
builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();

// 4. 图片生成服务（AGENT.md 中「调用图片生成服务进行生成」的那一层）
builder.AddImageService();

// 5. RabbitMQ 事件总线：只扫描当前程序集。
//    本程序集里只有一个消费者——ImageGenerationRequestedConsumer，
//    因此 Api 侧永远不会消费到生成任务消息，从结构上杜绝「双重消费」。
builder.AddEventBus(typeof(Program).Assembly);

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
