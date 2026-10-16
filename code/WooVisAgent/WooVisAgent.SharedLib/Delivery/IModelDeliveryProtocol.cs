using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 模型交付协议：决定「一次模型调用到底应该打向哪个 URL、用哪种请求体」。
///
/// 背景：早期实现把 endpoint 硬编码拼接成 <c>{endpoint}/v1/images/generations</c>，
/// 导致非 OpenAI 协议的模型（通义万相、即梦、ComfyUI、自建网关等）返回 404。
/// 这里把「路径解析」抽成一个可插拔的协议层，按供应商与 endpoint 形态智能适配。
///
/// 该抽象放在 SharedLib，供 ModelService（连通性测试）与 ImageService（图片生成）共用，
/// 避免在两个服务里各写一套路径拼接逻辑。
/// </summary>
public interface IModelDeliveryProtocol
{
    /// <summary>协议名称，用于日志与排查（如 openai-images / wanx / passthrough）</summary>
    string Name { get; }

    /// <summary>是否匹配该模型配置</summary>
    bool Matches(ModelConfig model);

    /// <summary>
    /// 把模型配置里的 endpoint 解析成真正要请求的 URL。
    /// 返回空表示无法解析（应由调用方给出明确错误，而不是继续硬拼路径）。
    /// </summary>
    string? ResolveRequestUrl(ModelConfig model);
}

/// <summary>
/// 协议解析器：按顺序挑选第一个匹配的协议。
/// 顺序很重要——更具体、更少歧义的协议要排在前面。
/// </summary>
public sealed class ModelDeliveryProtocolResolver
{
    private readonly IReadOnlyList<IModelDeliveryProtocol> _protocols;

    public ModelDeliveryProtocolResolver(IEnumerable<IModelDeliveryProtocol> protocols)
    {
        _protocols = protocols.ToList();
    }

    /// <summary>解析请求 URL；没有任何协议能匹配时返回 null（调用方报错，避免硬拼导致 404）</summary>
    public string? ResolveRequestUrl(ModelConfig model)
    {
        foreach (var protocol in _protocols)
        {
            if (protocol.Matches(model))
                return protocol.ResolveRequestUrl(model);
        }

        return null;
    }

    /// <summary>解析并附带命中的协议名，便于日志与排查</summary>
    public string? ResolveRequestUrl(ModelConfig model, out string? protocolName)
    {
        foreach (var protocol in _protocols)
        {
            if (protocol.Matches(model))
            {
                protocolName = protocol.Name;
                return protocol.ResolveRequestUrl(model);
            }
        }

        protocolName = null;
        return null;
    }
}
