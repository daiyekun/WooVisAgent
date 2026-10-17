using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.IdentityService.Options;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 集成测试宿主：真实走一遍 ASP.NET Core 管道（认证、授权、模型校验、MediatR 用例），
/// 仅把数据存储换成 EF InMemory，避免依赖 PostgreSQL。
/// </summary>
/// <remarks>
/// 配置分两段处理，原因是读取时机不同：
/// 1. <c>AddEfCore()/AddJwtAuthentication()</c> 在 <c>WebApplication.CreateBuilder</c> 之后立即读配置，
///    早于 WebApplicationFactory 注入配置项，因此这类开关走进程环境变量（所有测试工厂取值一致）；
/// 2. 身份用例开关（IdentityServiceOptions）在请求期才解析，可在 ConfigureTestServices 里整体替换，
///    便于不同测试类使用不同取值而互不干扰。
/// </remarks>
public class AuthApiFactory : WebApplicationFactory<Program>
{
    private static readonly object Gate = new();
    private static bool _environmentPrepared;

    /// <summary>本宿主专用的内存库名，保证不同测试宿主的用户数据互不可见</summary>
    protected string DatabaseName { get; } = $"woovisagent-tests-{Guid.NewGuid():N}";

    public AuthApiFactory() => PrepareEnvironment();

    private static void PrepareEnvironment()
    {
        lock (Gate)
        {
            if (_environmentPrepared)
                return;

            Set("Database__Provider", "InMemory");
            Set("Database__AutoInitialize", "true");

            // 事件总线整个关掉。
            //
            // 关键原因：Wolverine 在宿主<b>启动阶段</b>就连 broker，
            // 连不上会一直重连，表现为测试整体卡死
            // （本次踩过：84 个用例只跑出 3 个，耗时 4 分 46 秒）。
            //
            // 传一个占位连接串不够——照样会去连。必须让 AddEventBus 直接 return。
            // 这个开关只在测试里用，生产环境不设置，走的永远是 RabbitMQ。
            Set("EventBus__Enabled", "false");

            Set("Jwt__Issuer", "WooVisAgent.Tests");
            Set("Jwt__Audience", "WooVisAgent.Tests");
            Set("Jwt__SigningKey", "woovisagent-tests-signing-key-0123456789");
            Set("Jwt__ExpireMinutes", "60");

            _environmentPrepared = true;
        }
    }

    private static void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // 宿主启动前写入，Program.Main 里的 AddEfCore 会立即读取该配置
        Set("Database__Name", DatabaseName);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IdentityServiceOptions>();
            services.AddSingleton(CreateIdentityOptions());

            // 用 NoOp 实现替换两个依赖消息总线的组件，测试就不会去连 RabbitMQ。
            //
            // 不这么做的后果：Wolverine 在宿主启动时就去建队列，
            // 连不上就一直重连，测试要么卡死要么整体超时（本次踩过）。
            // 只把「发消息」这一步摘掉，Wolverine 本身仍照常注册，
            // 保证「测试跑的代码路径」与生产一致，只是消息没有真的发出去。
            services.RemoveAll<IGenerationTaskDispatcher>();
            services.AddSingleton<IGenerationTaskDispatcher, NoOpGenerationTaskDispatcher>();

            services.RemoveAll<IGenerationTaskNotifier>();
            services.AddSingleton<IGenerationTaskNotifier, NoOpGenerationTaskNotifier>();
        });
    }

    /// <summary>子类可覆盖，用于验证不同身份配置下的行为</summary>
    protected virtual IdentityServiceOptions CreateIdentityOptions() => new()
    {
        RequireEmailCode = false,
        EmailCodeExpireSeconds = 300,
        ExposeEmailCodeInResponse = true,
        DefaultRole = "User",
    };
}

/// <summary>
/// 什么都不做的派发器。任务照常落库，只是不往消息队列发。
/// </summary>
internal sealed class NoOpGenerationTaskDispatcher : IGenerationTaskDispatcher
{
    public Task DispatchAsync(GenerationTask task, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// 什么都不做的通知器。原本要把任务状态发到总线再由 Api 转成 SignalR 推送，
/// 测试里不关心推送，只关心任务与消息有没有正确落库。
/// </summary>
internal sealed class NoOpGenerationTaskNotifier : IGenerationTaskNotifier
{
    public Task NotifyAsync(
        GenerationTask task,
        IReadOnlyList<TaskAsset>? outputs = null,
        string? message = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>强制校验邮箱验证码的宿主</summary>
public class EmailCodeRequiredFactory : AuthApiFactory
{
    protected override IdentityServiceOptions CreateIdentityOptions() => new()
    {
        RequireEmailCode = true,
        EmailCodeExpireSeconds = 300,
        ExposeEmailCodeInResponse = true,
        DefaultRole = "User",
    };
}
