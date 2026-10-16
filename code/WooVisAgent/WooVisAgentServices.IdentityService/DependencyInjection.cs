using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WooVisAgent.Services.Common.Behaviors;
using WooVisAgentServices.IdentityService.Contracts;
using WooVisAgentServices.IdentityService.Options;
using WooVisAgentServices.IdentityService.Services;

namespace WooVisAgentServices.IdentityService;

public static class DependencyInjection
{
    /// <summary>
    /// 注册身份用例：MediatR 用例与权限行为、JWT 签发器、验证码服务、身份配置项。
    /// 依赖 UserManager/RoleManager，需宿主先调用 AddEfCore 完成 Identity + EF 存储注册。
    /// </summary>
    public static void AddIdentityService(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        // 同时注册为单例，供 JwtBearer 校验与用例注入使用
        builder.Services.AddSingleton(JwtOptions.FromConfiguration(configuration));
        builder.Services.AddSingleton(IdentityServiceOptions.FromConfiguration(configuration));

        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            // 权限校验行为：识别 [AuthorizeRequirement] 并基于 ICurrentUser 判定
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
        });

        builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        builder.Services.AddSingleton<IEmailCodeService, InMemoryEmailCodeService>();
    }
}
