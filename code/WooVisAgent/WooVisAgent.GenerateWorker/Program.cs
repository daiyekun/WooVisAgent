using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WooVisAgent.EventBus;

Console.WriteLine("Hello, World!");

var builder = Host.CreateApplicationBuilder(args);
// 1. 添加 Aspire 服务默认配置 (OpenTelemetry, HealthChecks 等)
builder.AddServiceDefaults();

// 2. 注册数据库上下文 (PostgreSQL)
// 这里的连接字符串名称需与 AppHost 中定义的一致
//builder.AddNpgsqlDbContext<AiCopilotDbContext>("ai-copilot");

// 3. 注册文件存储服务
// 必须与 HttpApi 使用相同的存储实现，确保能读取到上传的文件
//builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();

// 4. 注册事件总线 (RabbitMQ)
// 将自动扫描当前程序集下的 Consumer
builder.AddEventBus(typeof(Program).Assembly);

var host = builder.Build();
host.Run();
