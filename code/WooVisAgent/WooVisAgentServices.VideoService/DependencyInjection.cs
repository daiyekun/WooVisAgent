using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.VideoService.Contracts;
using WooVisAgentServices.VideoService.Delivery;
using WooVisAgentServices.VideoService.Services;
using WooVisAgentServices.VideoService.Support;

namespace WooVisAgentServices.VideoService;

/// <summary>
/// 视频生成服务的注册入口。与 <c>ImageService.DependencyInjection</c> 结构一致。
///
/// 这个程序集同样被两个宿主引用：
/// <list type="bullet">
///   <item>WooVisAgent.Api —— 提供提交 / 查询用例，提交后把消息发到 RabbitMQ</item>
///   <item>WooVisAgent.GenerateWorker —— 消费队列后调用 <see cref="IVideoGenerationService" /> 真正生成视频</item>
/// </list>
/// </summary>
public static class DependencyInjection
{
    /// <summary>调用视频模型接口所使用的 HttpClient 名称</summary>
    public const string VideoGenerationHttpClientName = "WooVisAgent.VideoGeneration";

    public static void AddVideoService(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        // 雪花主键与 ID 生成器在 ImageService 里已用 TryAdd 注册过，
        // 这里同样 TryAdd，两个服务都注册时只保留一份实例。
        builder.Services.TryAddSingleton(new SnowflakeIdOptions
        {
            DatacenterId = ParseInt(configuration["Snowflake:DatacenterId"], 0),
            WorkerId = ParseInt(configuration["Snowflake:WorkerId"], 0),
            EpochMilliseconds = ParseLong(configuration["Snowflake:EpochMilliseconds"], 0),
        });
        builder.Services.TryAddSingleton<ISnowflakeIdGenerator, SnowflakeIdGenerator>();

        // 视频生成比图片慢得多（通常 1-5 分钟），超时必须放宽。
        //
        // 注意这个值要和 ServiceDefaults 的弹性管道对齐：
        // HttpClient.Timeout 是最外层兜底（一旦触发会砍掉整个请求，含管道内重试），
        // 真正的超时判定交给 Polly，因此这里留到 20 分钟，
        // 正常情况下永远先由 <see cref="VideoDeliveryFramework.MaxPollMinutes" /> 的 15 分钟窗口收敛。
        builder.Services.AddHttpClient(VideoGenerationHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WooVisAgent-VideoGeneration/1.0");
        });

        // ffmpeg / ffprobe 是可选依赖：路径可在配置里覆盖（容器镜像常把 ffmpeg 装在非 PATH 目录），
        // 找不到时视频照样生成成功，只是没有封面与时长信息。
        builder.Services.Configure<VideoMediaOptions>(configuration.GetSection("VideoMedia"));

        builder.Services.AddSingleton<IVideoMediaInspector, FfmpegVideoMediaInspector>();
        builder.Services.AddSingleton<IVideoDeliveryFramework, VideoDeliveryFramework>();
        builder.Services.AddSingleton<IVideoGenerationService, ConfiguredVideoGenerationService>();

        // MediatR 核心服务与管道行为已在 AddIdentityService 注册，
        // 这里只追加本程序集内的用例，避免重复注册 AuthorizationBehavior。
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static long ParseLong(string? value, long fallback)
        => long.TryParse(value, out var parsed) ? parsed : fallback;
}