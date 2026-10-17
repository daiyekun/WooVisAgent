namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 会话内的一条消息。
///
/// 一次生成会在对话流里落两条消息：
/// <list type="number">
///   <item>用户消息（role=User）—— 提示词 + 参考图；</item>
///   <item>助手消息（role=Assistant）—— 挂着这次生成的产物。</item>
/// </list>
/// 两条通过 <see cref="PairMessageId" /> 互指，因此从任意一侧都能定位到另一侧；
/// 助手消息上的 <see cref="TaskId" /> 则是双击任务后精确定位高亮的依据。
/// </summary>
public class ConversationMessage : BaseEntity<long>, IAggregateRoot, IAggregateRoot<long>
{
    /// <summary>所属会话</summary>
    public long ConversationId { get; set; }

    public string OwnerId { get; set; } = string.Empty;

    /// <summary>user / assistant，与前端 ChatMessage.role 对齐</summary>
    public string Role { get; set; } = "user";

    /// <summary>消息正文。用户消息是提示词，助手消息是过程说明</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 本条消息关联的生成任务。
    /// 用户消息为空，助手消息指向它产出的任务。
    /// </summary>
    public long? TaskId { get; set; }

    /// <summary>
    /// 同一次生成里的配对消息 id（用户消息与助手消息互指）。
    /// 图生图时还要靠它追溯「这张是从哪一轮改过来的」。
    /// </summary>
    public string? PairMessageId { get; set; }

    /// <summary>参考图 / 产物地址 JSON（字符串数组），用户消息上是参考图，助手消息上是产物</summary>
    public string AssetsJson { get; set; } = "[]";

    /// <summary>
    /// 本轮生成参数快照 JSON。
    /// 「重跑」「作为参考图继续」都需要复现当时的宽高、步数、CFG、种子，
    /// 这些不能只存在浏览器里——换个会话再回来重跑也得能还原。
    /// </summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>图生图来源任务 id：这一轮是拿哪条消息的产物去改的</summary>
    public long? SourceTaskId { get; set; }

    /// <summary>会话内的顺序号，从 1 开始递增，保证历史回放顺序稳定</summary>
    public int Seq { get; set; }

    /// <summary>失败原因，仅助手消息可能非空</summary>
    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
