namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 创作会话：一条对话流。
///
/// 之前对话页的会话只存在于浏览器内存里，刷新即丢，也无法从任务反查上下文。
/// 落库之后才能支撑三个能力：
///   <list type="bullet">
///     <item>侧边栏「最近会话」与「更多会话」列表；</item>
///     <item>从任意任务反查它属于哪段对话（双击任务定位到聊天区域）；</item>
///     <item>一次会话内多轮生成对应多个任务的历史回放。</item>
///   </list>
///
/// 会话与任务是 1:N：每提交一次生成（文生图/图生图各算一次）就归到当前会话下，
/// 见 <see cref="GenerationTask.ConversationId" />。
/// </summary>
public class Conversation : BaseEntity<long>, IAggregateRoot, IAggregateRoot<long>
{
    /// <summary>会话标题，由首条提示词截取生成，用户可在聊天页改名</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>会话归属用户（IdentityUser.Id），实现用户间数据隔离</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// 会话内出现过的生成类型。
    /// 会话列表要按图片 / 视频分类展示，而一次会话可能两种都有，
    /// 此时统一记为 <see cref="GenerationTaskType.Image" />（以先出现的类型为准），
    /// 避免一个会话同时落在两个分类下导致计数对不上。
    /// </summary>
    public GenerationTaskType Type { get; set; } = GenerationTaskType.Image;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 最后修改时间（新建消息、刷新产物都会推进它）。
    /// 侧边栏与列表默认按它倒序排，用户心智里「最近会话」就该是最近聊过的。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>会话内的消息条数，列表页直接展示，避免为每行都做一次 count 查询</summary>
    public int MessageCount { get; set; }

    /// <summary>会话内已生成的任务数（生成一次算一个任务）</summary>
    public int TaskCount { get; set; }
}
