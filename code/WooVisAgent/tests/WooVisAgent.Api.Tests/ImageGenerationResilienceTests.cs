using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly.Timeout;
using WooVisAgent.ServiceDefaults;
using WooVisAgentServices.ImageService;
using ImageServiceDi = WooVisAgentServices.ImageService.DependencyInjection;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 图片生成任务的「30 秒超时」回归测试。
///
/// 背景：Aspire 的 AddServiceDefaults 会给所有 HttpClient 挂上标准弹性管道，
/// 其 TotalRequestTimeout 默认值是 30 秒。图片生成动辄几十秒到几分钟，
/// 于是请求在真正返回前就被 Polly 掐断，任务列表里出现：
/// “The operation didn't complete within the allowed timeout of '00:00:30'.”
///
/// 这三条测试分别锁住：
/// <list type="number">
///   <item>框架默认值确实是 30 秒（即根因），而本项目的配置把它放长了；</item>
///   <item>通过 ConfigureHttpClientDefaults 传入的超时配置确实会作用到命名 HttpClient（否则改了也没用）；</item>
///   <item>端到端：图片生成客户端能扛过 30 秒（标记为 Slow，可用 --filter 排除）。</item>
/// </list>
/// </summary>
public class ImageGenerationResilienceTests
{
    private const string ClientName = ImageServiceDi.ImageGenerationHttpClientName;

    [Fact]
    public void 弹性超时配置必须覆盖模板默认的30秒()
    {
        // 1) 先确认真实存在的默认值——这就是线上报错的来源
        var defaults = new HttpStandardResilienceOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), defaults.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.AttemptTimeout.Timeout);

        // 2) 再确认本项目用真实配置方法改掉了它
        var configured = new HttpStandardResilienceOptions();
        HttpResilienceDefaults.Apply(configured);

        Assert.True(
            configured.TotalRequestTimeout.Timeout > TimeSpan.FromSeconds(30),
            $"总超时必须大于模板默认的 30 秒，当前为 {configured.TotalRequestTimeout.Timeout}");
        Assert.True(
            configured.AttemptTimeout.Timeout > TimeSpan.FromSeconds(30),
            $"单次尝试超时必须大于 30 秒，当前为 {configured.AttemptTimeout.Timeout}");

        // 3) 框架的配置校验约束，改超时时必须一并满足，否则启动时直接抛异常
        Assert.True(configured.TotalRequestTimeout.Timeout >= configured.AttemptTimeout.Timeout);
        Assert.True(configured.CircuitBreaker.SamplingDuration >= configured.AttemptTimeout.Timeout * 2);
    }

    /// <summary>
    /// 证明 ConfigureHttpClientDefaults 里配置的标准弹性管道真的作用到了命名客户端。
    /// 这里用 3 秒总超时（而不是生产的 5 分钟）来快速验证同一条装配路径。
    /// </summary>
    [Fact]
    public async Task 弹性管道配置会作用到命名HttpClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(3);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
        }));

        services.AddHttpClient(ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new DelayHttpMessageHandler(TimeSpan.FromSeconds(15)));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => client.GetAsync("https://example.invalid/probe"));

        stopwatch.Stop();

        // 生效的应当是管道里的 3 秒，而不是 HttpClient 自己的 15 秒延迟或框架默认的 30 秒
        Assert.Contains("00:00:03", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"实际耗时 {stopwatch.Elapsed}");
    }

    /// <summary>
    /// 端到端：用真实的 AddServiceDefaults + AddImageService 组装容器，
    /// 让「模型接口」睡 32 秒再返回。修复前这里会在 30 秒被掐断，修复后必须成功。
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task 图片生成客户端能扛过30秒()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.AddImageService();

        // 用假的模型接口替换真实网络调用：睡 32 秒，再返回一份合法的 OpenAI Images 响应
        builder.Services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelayHttpMessageHandler(TimeSpan.FromSeconds(32)));

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostAsync(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis",
            new StringContent("{\"model\":\"qwen-image\"}", Encoding.UTF8, "application/json"));
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed > TimeSpan.FromSeconds(30),
            $"这条测试的意义就是耗时超过 30 秒，实际只有 {stopwatch.Elapsed}");
    }

    /// <summary>固定延迟后返回 200 的假处理器，用来模拟「慢模型接口」</summary>
    private sealed class DelayHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"url\":\"https://example.invalid/image.png\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
