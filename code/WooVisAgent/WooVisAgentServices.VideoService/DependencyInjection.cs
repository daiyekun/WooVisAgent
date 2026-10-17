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

        // 这个值是<b>最外层兜底</b>，要同时大于下面两个值：
        //   ① 弹性管道总超时（5 分钟）——否则最外层会先于管道掐断请求；
        //   ② 轮询窗口 MaxPollMinutes（20 分钟）——保证"超时由业务代码抛出可读中文"
        //      这条不变量成立，而不是被传输层先抛一句 HttpClient.Timeout。
        //
        // 但单靠它挡不住"一次请求挂起把整个窗口拖过去"：
        // 轮询阶段现在自己带 90 秒的请求级超时 + 到点取消的窗口令牌，
        // 所以这里的值不再参与轮询超时判定，只覆盖下载成品视频的耗时。
        builder.Services.AddHttpClient(VideoGenerationHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(25);
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