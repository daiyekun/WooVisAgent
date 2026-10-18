using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WooVisAgentServices.AgentService.Options;
using WooVisAgentServices.AgentService.Services;

namespace WooVisAgentServices.AgentService;

/// <summary>
/// Agent 能力注册入口。
///
/// 这里**只做注册，不对外发任何请求**。配置缺失、密钥失效等问题的暴露点都在请求期，
/// 因此即使一个语言模型都没配，宿主照样启动、登录、图片、视频全部不受影响。
/// </summary>
public static class DependencyInjection
{
    public static void AddAgentService(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AgentOptions.SectionName);
        var options = new AgentOptions
        {
            Enabled = ParseBool(section["Enabled"], true),
            TimeoutSeconds = ParseInt(section["TimeoutSeconds"], 90),
            MaxInputChars = ParseInt(section["MaxInputChars"], 4000),
            EnableToolCalling = ParseBool(section["EnableToolCalling"], true),
        };

        builder.Services.AddSingleton(options);

        // 依赖 IRepository（Scoped），因此两个服务都必须是 Scoped
        builder.Services.AddScoped<ILlmClientResolver, LlmClientResolver>();
        builder.Services.AddScoped<IWooVisDirector, WooVisDirector>();

        // 与 ModelService / ImageService 相同的做法：只追加本程序集内的用例，
        // MediatR 核心与管道行为（授权、Tracing）由 AddIdentityService 统一注册
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;
}
