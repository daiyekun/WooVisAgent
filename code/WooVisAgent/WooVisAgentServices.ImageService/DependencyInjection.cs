using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Delivery;
using WooVisAgentServices.ImageService.Services;

namespace WooVisAgentServices.ImageService;

/// <summary>
/// 图片生成服务的注册入口。
///
/// 这个程序集同时被两个宿主引用：
/// <list type="bullet">
///   <item>WooVisAgent.Api —— 提供提交 / 查询 / 取消用例，提交后把消息发到 RabbitMQ</item>
///   <item>WooVisAgent.GenerateWorker —— 消费队列后调用 <see cref="Contracts.IImageGenerationService" /> 真正生成图片</item>
/// </list>
/// 依赖宿主先调用 AddEfCore 完成 DbContext / 仓储注册，调用 AddEventBus 完成消息总线注册。
/// </summary>
public static class DependencyInjection
{
    /// <summary>调用模型方图片接口所使用的 HttpClient 名称</summary>
    public const string ImageGenerationHttpClientName = "WooVisAgent.ImageGeneration";

    public static void AddImageService(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        // 雪花主键。Api 侧通常已由 AddModelService 注册，这里用 TryAdd 兜底，
        // 让 GenerateWorker 单独启动时也能拿到 id 生成器。
        // 多实例部署时务必用 Snowflake:WorkerId 区分节点，否则跨进程可能撞 id。
        builder.Services.TryAddSingleton(new SnowflakeIdOptions
        {
            DatacenterId = ParseInt(configuration["Snowflake:DatacenterId"], 0),
            WorkerId = ParseInt(configuration["Snowflake:WorkerId"], 0),
            EpochMilliseconds = ParseLong(configuration["Snowflake:EpochMilliseconds"], 0),
        });
        builder.Services.TryAddSingleton<ISnowflakeIdGenerator, SnowflakeIdGenerator>();

        // 图片生成耗时可能较长，超时给足。
        //
        // 注意这里的时间必须和 ServiceDefaults 里的弹性管道配置对齐：
        // HttpClient.Timeout 是「最外层」兜底（一旦触发会直接砍掉整个请求，包括管道内的重试），
        // 真正的超时判定交给 Polly（单次尝试 4 分钟 / 总 5 分钟），
        // 所以这里留到 5 分钟，正常情况下永远不会先于弹性管道触发。
        // 只调这一个参数是修不好 30 秒超时的——30 秒来自弹性管道的 TotalRequestTimeout 默认值。
        builder.Services.AddHttpClient(ImageGenerationHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WooVisAgent-ImageGeneration/1.0");
        });

        builder.Services.AddSingleton<IImageGenerationService, ConfiguredImageGenerationService>();
        builder.Services.AddScoped<IGenerationTaskDispatcher, GenerationTaskDispatcher>();
        builder.Services.AddScoped<IGenerationTaskNotifier, GenerationTaskNotifier>();

        // 模型交付协议解析器：按供应商/endpoint 形态把「接口地址」解析成真正的生成 URL。
        // 协议清单与顺序由 ModelDeliveryProtocols.CreateDefault() 统一维护；
        // 用 TryAdd 兜底：Api 同时注册 ImageService 与 ModelService 时，只保留一份实例。
        builder.Services.TryAddSingleton(ModelDeliveryProtocols.CreateDefault());

        // 模型交付框架：统一编排图片生成与连通性测试，是面向 Microsoft Agent Framework 的抽象落点。
        builder.Services.AddSingleton<IModelDeliveryFramework, ModelDeliveryFramework>();

        // MediatR 核心服务与管道行为已在 AddIdentityService 注册，
        // 这里只追加本程序集内的用例，避免重复注册 AuthorizationBehavior。
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static long ParseLong(string? value, long fallback)
        => long.TryParse(value, out var parsed) ? parsed : fallback;
}
