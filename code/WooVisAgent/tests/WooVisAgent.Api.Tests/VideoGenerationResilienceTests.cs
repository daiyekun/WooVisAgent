using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using WooVisAgent.ServiceDefaults;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgentServices.VideoService;
using VideoServiceDi = WooVisAgentServices.VideoService.DependencyInjection;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 视频生成的 HTTP 超时与弹性管道回归测试。
///
/// 背景与 <c>ImageGenerationResilienceTests</c> 同源：Aspire 的 AddServiceDefaults
/// 默认「单次尝试 10 秒、总超时 30 秒」。视频生成比图片更慢，
/// 一旦被 Polly 在 30 秒处掐断，任务列表里就会出现
/// "The operation didn't complete within the allowed timeout of '00:00:30'"，
/// 而且因为轮询每 3 秒发一次请求，表现为「跑着跑着就挂了」，极难定位。
///
/// 这里锁住三件事：
/// <list type="number">
///   <item>框架默认值确实是 30 秒（即根因），而本项目把它放长了；</item>
///   <item>视频命名 HttpClient 的外层超时（20 分钟）大于弹性管道总超时（5 分钟），
///         因此永远由后者先收敛，不会出现"两层超时互相打架"的难查现象；</item>
///   <item>轮询上限（15 分钟）小于 HttpClient 超时（20 分钟），
///         保证超时一定由业务逻辑抛出可读的中文，而不是以裸异常收场。</item>
/// </list>
/// </summary>
public class VideoGenerationResilienceTests
{
    [Fact]
    public void 弹性超时配置必须覆盖模板默认的30秒()
    {
        var defaults = new HttpStandardResilienceOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.TotalRequestTimeout.Timeout);

        var configured = new HttpStandardResilienceOptions();
        HttpResilienceDefaults.Apply(configured);

        Assert.True(configured.TotalRequestTimeout.Timeout > TimeSpan.FromSeconds(30));
        Assert.True(configured.AttemptTimeout.Timeout > TimeSpan.FromSeconds(30));

        // 框架自身的配置校验约束，改超时时必须一并满足
        Assert.True(configured.TotalRequestTimeout.Timeout >= configured.AttemptTimeout.Timeout);
        Assert.True(configured.CircuitBreaker.SamplingDuration >= configured.AttemptTimeout.Timeout * 2);
    }

    [Fact]
    public async Task 视频HttpClient超时应长于弹性管道总超时()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddVideoService();

        // 取出实际配置好的 HttpClient，验证装配结果而非源码里的字面量
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient(VideoServiceDi.VideoGenerationHttpClientName);
        var timeout = client.Timeout;

        Assert.True(
            timeout > HttpResilienceDefaults.TotalRequestTimeout,
            "HttpClient.Timeout 必须大于弹性管道的总超时，" +
            $"否则最外层会先于管道掐断请求。当前 HttpClient={timeout}，管道={HttpResilienceDefaults.TotalRequestTimeout}");

        client.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public void 轮询上限必须小于HttpClient超时()
    {
        var pollLimit = TimeSpan.FromMinutes(VideoGenerationFrameworkLimits.MaxPollMinutes);
        var httpTimeout = TimeSpan.FromMinutes(20);

        // 视频提交模型地址若不可达，理想情况是业务代码在轮询窗口耗尽后
        // 抛出「已停止等待」这种可读中文，而不是由传输层先抛一句
        // "The request was canceled due to the configured HttpClient.Timeout"
        Assert.True(
            pollLimit < httpTimeout,
            $"轮询上限 {pollLimit} 应小于 HttpClient 超时 {httpTimeout}，" +
            "这样超时一定由业务逻辑给出可读提示");
    }

    [Fact]
    public async Task 视频接口不可达时应抛出可读中文而不是裸异常()
    {
        var builder = Host.CreateApplicationBuilder();

        // 指向一个必然连不通的地址（本地 discard 端口 9）。
        // 文件存储用最小桩即可：本用例只验证"连不通时错误是否可读"，
        // 不会走到落盘那一步，而 VideoDeliveryFramework 是构造函数注入它的
        builder.Services.AddSingleton<IFileStorageService, NoOpFileStorageService>();
        builder.AddVideoService();
        using var host = builder.Build();

        var framework = host.Services
            .GetRequiredService<WooVisAgentServices.VideoService.Contracts.IVideoGenerationService>();

        var request = new WooVisAgentServices.VideoService.Contracts.VideoGenerationRequest(
            TaskId: 1,
            OwnerId: "tester",
            Prompt: "测试提示词",
            NegativePrompt: null,
            FirstFrame: null,
            Resolution: "1280x720",
            DurationSeconds: 5,
            Fps: null,
            Seed: null,
            ExtraParams: new Dictionary<string, object?>(),
            Model: new WooVisAgent.SharedLib.Domain.ModelConfig
            {
                Id = 1,
                OwnerId = "tester",
                Name = "不可达模型",
                Provider = "测试",
                Endpoint = "http://127.0.0.1:9/unreachable",
                ModelKey = "test-model",
                ApiKey = "sk-test",
            });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => framework.GenerateAsync(request));

        // 关键断言：错误必须是可读中文，而不是把 "Connection refused (127.0.0.1:9)"
        // 这种裸传输层错误直接甩给用户
        Assert.False(string.IsNullOrWhiteSpace(exception.Message), "失败时必须给出可读原因");

        var readable = new[] { "视频模型", "调用视频模型接口", "视频生成" };

        Assert.True(
            readable.Any(word => exception.Message.Contains(word, StringComparison.Ordinal)),
            $"错误信息应为可读中文，实际为：{exception.Message}");
    }
}

/// <summary>
/// 供测试引用的框架常量。
/// 单独包一层是为了让 <see cref="VideoDeliveryFramework.MaxPollMinutes" />
/// 这个"对外承诺的行为上限"成为可被测试断言的契约，而不是散落在实现里的字面量。
/// </summary>
internal static class VideoGenerationFrameworkLimits
{
    /// <summary>视频生成的轮询上限（分钟）</summary>
    public const int MaxPollMinutes = WooVisAgentServices.VideoService.Delivery.VideoDeliveryFramework.MaxPollMinutes;
}

/// <summary>
/// 最小文件存储桩：本用例不会走到落盘，只需要它能完成依赖注入。
/// </summary>
internal sealed class NoOpFileStorageService : IFileStorageService
{
    public Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        => Task.FromResult($"/uploads/{fileName}");

    public Task<Stream?> GetAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream?>(null);

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}