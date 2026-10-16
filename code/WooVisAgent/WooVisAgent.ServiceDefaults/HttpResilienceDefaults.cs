using Microsoft.Extensions.Http.Resilience;

namespace WooVisAgent.ServiceDefaults;

/// <summary>
/// HTTP 弹性管道（重试 / 熔断 / 超时）的默认参数。
///
/// 为什么要单独放一个类：Aspire 模板给的默认值是「单次尝试 10 秒、总超时 30 秒」，
/// 而本应用的出站调用几乎都是模型调用——图片生成动辄几十秒到几分钟。
/// 沿用默认值会让请求在模型返回之前就被 Polly 掐断，任务列表里出现：
/// <c>The operation didn't complete within the allowed timeout of '00:00:30'.</c>
///
/// 调这些数值时必须同时满足框架的配置校验：
/// <list type="bullet">
///   <item>总超时 ≥ 单次尝试超时；</item>
///   <item>熔断采样窗口 ≥ 2 × 单次尝试超时。</item>
/// </list>
/// </summary>
public static class HttpResilienceDefaults
{
    /// <summary>单次尝试的最长等待时间。图片同步出图 1~3 分钟是常态，取 4 分钟。</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(4);

    /// <summary>一次出站调用（含重试）的最长总耗时。</summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>熔断采样窗口，需 ≥ 2 × <see cref="AttemptTimeout" />。</summary>
    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromMinutes(10);

    /// <summary>把上述参数套用到标准弹性管道上</summary>
    public static void Apply(HttpStandardResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalRequestTimeout;
        options.CircuitBreaker.SamplingDuration = CircuitBreakerSamplingDuration;

        // 写操作不重试：重新提交一次图片生成 = 重复计费。
        // 生成失败的多方案降级由 ModelDeliveryFramework 负责，重试由任务层（人工/重试接口）负责。
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.DisableForUnsafeHttpMethods();
    }
}
