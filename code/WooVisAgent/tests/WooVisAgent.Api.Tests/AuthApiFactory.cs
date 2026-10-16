using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WooVisAgentServices.IdentityService.Options;

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
