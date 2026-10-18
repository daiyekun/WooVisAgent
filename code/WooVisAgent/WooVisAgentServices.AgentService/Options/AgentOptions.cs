namespace WooVisAgentServices.AgentService.Options;

/// <summary>
/// Agent 模块开关。配置节：<c>Agent</c>。
///
/// 这里的默认值刻意保守：<see cref="Enabled"/> 为 <c>true</c> 只代表「允许使用」，
/// 真正能不能跑取决于用户有没有在「模型设置」里配一个启用的 <c>Llm</c> 模型。
/// 没配时接口返回可读的 400 提示，不会影响登录、图片、视频等既有链路。
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>总开关。关掉后 /api/agent/* 一律返回「未启用」</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>单次模型调用的超时（秒）。提示词润色属于短请求，默认 90 秒足够</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>用户输入的最大字符数，防止把整篇小说塞进 prompt 账单</summary>
    public int MaxInputChars { get; set; } = 4000;

    /// <summary>
    /// 是否向模型声明工具。少数网关（自建中转）不支持 function calling，会返回 400；
    /// 关闭后只是不再把用户已有的模型清单作为工具下发，功能仍可用。
    /// </summary>
    public bool EnableToolCalling { get; set; } = true;
}
