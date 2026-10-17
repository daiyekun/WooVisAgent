namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 作品（用户称之为"我的产品"）。
///
/// 来源有两种：
/// <list type="bullet">
///   <item>任务选秀：在任务列表点「选秀」，把该任务的全部产物收进作品库（<see cref="SourceTaskId" /> 非空）</item>
///   <item>手动上传：用户在作品库直接上传图片或视频（<see cref="SourceTaskId" /> 为空）</item>
/// </list>
///
/// 与 <see cref="TaskAsset" /> 的区别：TaskAsset 是任务的输入/输出，随任务存放在 JSON 字段里，
/// 任务删除就没了；本实体是独立聚合根，一经「选秀」入库即独立存在，任务被删除也不受影响。
///
/// 主键为 64 位雪花 ID，按用户隔离（<see cref="OwnerId" />）。
/// </summary>
public class MaterialAsset : BaseEntity<long>, IAggregateRoot, IAggregateRoot<long>
{
    /// <summary>作品归属用户（IdentityUser.Id）</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// 来源任务主键。手动上传的作品没有来源，此处为 null。
    /// 一个任务可对应多条作品（batchSize > 1 时每个产物各存一条）。
    /// </summary>
    public long? SourceTaskId { get; set; }

    /// <summary>来源任务名称快照，任务被删除后作品列表仍能展示出处</summary>
    public string? SourceTaskName { get; set; }

    /// <summary>来源产物在任务内的 ID，便于与 TaskAsset 对齐与去重</summary>
    public string? SourceAssetId { get; set; }

    /// <summary>image / video</summary>
    public string Type { get; set; } = "image";

    /// <summary>可直接在页面 &lt;img&gt; / &lt;video&gt; 中使用的访问地址</summary>
    public string Url { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    public string? FileName { get; set; }

    /// <summary>字节</summary>
    public long? FileSize { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>视频时长，单位秒</summary>
    public double? Duration { get; set; }

    /// <summary>分组名，前端按它做筛选页签</summary>
    public string? GroupName { get; set; }

    /// <summary>生成该作品所用的提示词快照</summary>
    public string? Prompt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}