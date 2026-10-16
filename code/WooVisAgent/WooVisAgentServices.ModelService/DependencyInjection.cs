using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ModelService.Providers;
using WooVisAgentServices.ModelService.Services;

namespace WooVisAgentServices.ModelService;

public static class DependencyInjection
{
    /// <summary>
    /// 注册模型配置用例：雪花主键生成器、连通性测试器、供应方模型清单，以及本程序集内的 MediatR 用例。
    /// 依赖宿主先调用 AddEfCore 完成 DbContext / 仓储注册。
    /// </summary>
    public static void AddModelService(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        // 数据库主键统一 64 位雪花 ID；多实例部署时用 Snowflake:WorkerId 区分节点
        builder.Services.AddSingleton(new SnowflakeIdOptions
        {
            DatacenterId = ParseInt(configuration["Snowflake:DatacenterId"], 0),
            WorkerId = ParseInt(configuration["Snowflake:WorkerId"], 0),
            EpochMilliseconds = ParseLong(configuration["Snowflake:EpochMilliseconds"], 0),
        });
        builder.Services.AddSingleton<ISnowflakeIdGenerator, SnowflakeIdGenerator>();

        builder.Services.AddHttpClient(ModelConnectivityTesterDefaults.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WooVisAgent-ModelConnectivity/1.0");
        });

        builder.Services.AddSingleton<IModelConnectivityTester, LightProbeModelConnectivityTester>();
        builder.Services.AddSingleton<IModelProviderCatalog, StaticModelProviderCatalog>();

        // 注意：MediatR 核心服务与管道行为已在 AddIdentityService 注册，
        // 这里只追加本程序集内的用例，避免重复注册 AuthorizationBehavior。
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static long ParseLong(string? value, long fallback)
        => long.TryParse(value, out var parsed) ? parsed : fallback;
}
