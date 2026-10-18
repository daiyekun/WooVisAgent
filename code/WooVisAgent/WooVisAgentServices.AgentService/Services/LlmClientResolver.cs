using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.AgentService.Options;

namespace WooVisAgentServices.AgentService.Services;

/// <summary>
/// 把「模型设置」里用户自己填的 <c>Llm</c> 配置解析成一个可用的 <see cref="IChatClient" />。
///
/// 设计上的两个约束：
/// <list type="number">
///   <item>绝不在启动期解析。配置可能不存在、密钥可能失效，构建宿主时不能因此失败。</item>
///   <item>解析失败必须给出可直接展示的中文原因，而不是抛异常穿透到 500。</item>
/// </list>
/// </summary>
public interface ILlmClientResolver
{
    /// <summary>为指定用户解析一个可用的语言模型客户端</summary>
    Task<LlmClientResolution> ResolveAsync(string ownerId, CancellationToken cancellationToken);
}

/// <summary>解析结果。<see cref="Client" /> 不为空表示可用，调用方负责释放</summary>
public sealed record LlmClientResolution(
    IChatClient? Client,
    ModelConfig? Config,
    string? FailureReason)
{
    public bool IsAvailable => Client is not null;

    public static LlmClientResolution Fail(string reason) => new(null, null, reason);
}

public class LlmClientResolver(
    IRepository<ModelConfig> repository,
    AgentOptions options,
    ILogger<LlmClientResolver> logger) : ILlmClientResolver
{
    public async Task<LlmClientResolution> ResolveAsync(string ownerId, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return LlmClientResolution.Fail("Agent 能力未启用（配置项 Agent:Enabled 为 false）。");

        var candidates = await repository.GetListAsync(
            x => x.OwnerId == ownerId && x.Category == ModelCategory.Llm && x.Enabled,
            cancellationToken);

        // 默认模型优先，其次用最近更新的那条。用户配了多个 Llm 时行为可预期
        var config = candidates
            .OrderByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

        if (config is null)
            return LlmClientResolution.Fail("还没有可用的语言模型。请到「模型设置」新增一条分类为「语言模型」的配置并启用。");

        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return LlmClientResolution.Fail($"语言模型「{config.Name}」缺少访问密钥，请补充后重试。");

        if (string.IsNullOrWhiteSpace(config.ModelKey))
            return LlmClientResolution.Fail($"语言模型「{config.Name}」缺少模型标识，请补充后重试。");

        var normalized = EndpointNormalizer.Normalize(config.Endpoint);
        if (normalized is null)
            return LlmClientResolution.Fail($"语言模型「{config.Name}」的接口地址不合法，应以 http:// 或 https:// 开头。");

        try
        {
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = normalized,
                    // 单次调用超时。放在客户端而不是外层 CTS，SDK 才能给出「超时」这个明确原因
                    NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 300)),
                });

            return new LlmClientResolution(openAiClient.GetChatClient(config.ModelKey).AsIChatClient(), config, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "构建语言模型客户端失败，配置 {ConfigId}", config.Id);
            return LlmClientResolution.Fail($"语言模型「{config.Name}」初始化失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 把用户填的接口地址整理成 OpenAI SDK 需要的 base endpoint。
///
/// 常见三种写法都要能承接：
/// <list type="bullet">
///   <item><c>https://dashscope.aliyuncs.com/compatible-mode/v1</c>：已带版本段，原样保留</item>
///   <item><c>https://api.deepseek.com</c>：只有主机名，补 <c>/v1</c></item>
///   <item><c>https://api.deepseek.com/v1/</c>：去掉尾部斜杠</item>
/// </list>
/// 不补 <c>/v1</c> 的话 SDK 会拼成 <c>https://api.deepseek.com/chat/completions</c>，直接 404。
/// </summary>
public static class EndpointNormalizer
{
    public static Uri? Normalize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');

        // 路径为空或仅一个 / 时补上版本段；已有路径（/compatible-mode/v1、/v1、/proxy/v1）一律尊重用户填写
        if (path.Length == 0)
            path = "/v1";

        return new UriBuilder(uri) { Path = path, Query = string.Empty }.Uri;
    }
}
