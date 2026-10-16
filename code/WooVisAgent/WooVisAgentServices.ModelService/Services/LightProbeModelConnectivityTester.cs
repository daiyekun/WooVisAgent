using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ModelService.Dtos;

namespace WooVisAgentServices.ModelService.Services;

/// <summary>模型连通性测试</summary>
public interface IModelConnectivityTester
{
    Task<ModelTestResultDto> TestAsync(
        string? name,
        ModelCategory category,
        string? endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 「本地校验 + 轻量探测」实现：
/// 1. 先做离线校验（地址是否填写、格式是否合法、云端地址是否配了密钥）；
/// 2. 通过后对该地址发一次 HEAD 探测，只要能拿到任意 HTTP 响应即视为地址可达。
/// 全程不会提交任何真实的生成请求，也不消耗模型额度。
/// </summary>
public sealed class LightProbeModelConnectivityTester(
    IHttpClientFactory httpClientFactory,
    ILogger<LightProbeModelConnectivityTester> logger) : IModelConnectivityTester
{
    /// <summary>探测超时时间，避免个别地址长时间挂住请求</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<ModelTestResultDto> TestAsync(
        string? name,
        ModelCategory category,
        string? endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "该模型" : $"「{name.Trim()}」";

        if (string.IsNullOrWhiteSpace(endpoint))
            return new ModelTestResultDto(false, null, $"{label}尚未配置接口地址，无法连通。");

        var address = endpoint.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ModelTestResultDto(false, null, "接口地址格式不正确，需以 http:// 或 https:// 开头。");

        if (!IsLocalAddress(uri) && string.IsNullOrWhiteSpace(apiKey))
            return new ModelTestResultDto(false, null, "云端模型服务未配置 API Key，请补充密钥后再测试。");

        var client = httpClientFactory.CreateClient(ModelConnectivityTesterDefaults.HttpClientName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            stopwatch.Stop();

            // 只要拿到了 HTTP 响应，就说明地址可达、DNS 与 TLS 链路正常
            return new ModelTestResultDto(
                true,
                (int)stopwatch.ElapsedMilliseconds,
                $"地址可达（HTTP {(int)response.StatusCode}）");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LogWarning("模型 {Endpoint} 连通性探测超时", address);
            return new ModelTestResultDto(
                false,
                (int)stopwatch.ElapsedMilliseconds,
                $"探测超时（{ProbeTimeout.TotalSeconds:0} 秒），请检查接口地址与网络。");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            logger.LogWarning(exception, "模型 {Endpoint} 连通性探测失败", address);
            return new ModelTestResultDto(
                false,
                (int)stopwatch.ElapsedMilliseconds,
                $"连接失败：{exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            logger.LogError(exception, "模型 {Endpoint} 连通性探测异常", address);
            return new ModelTestResultDto(false, null, $"测试异常：{exception.Message}");
        }
    }

    private static bool IsLocalAddress(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.StartsWith("127.", StringComparison.Ordinal)
               || host.StartsWith("192.168.", StringComparison.Ordinal)
               || host.StartsWith("10.", StringComparison.Ordinal);
    }
}

/// <summary>连通性测试用到的常量</summary>
public static class ModelConnectivityTesterDefaults
{
    public const string HttpClientName = "model-connectivity";
}
