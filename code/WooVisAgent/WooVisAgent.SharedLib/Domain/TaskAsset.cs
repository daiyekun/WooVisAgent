namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 任务素材：既可以是任务的输入素材（参考图、首帧图），也可以是任务的生成产物。
///
/// 它不是独立聚合根，而是随 <see cref="GenerationTask" /> 一起读写的值对象，
/// 以 JSON 形式存储在 InputsJson / OutputsJson 字段中，字段命名与前端
/// WooVisAgent.Web/src/types/index.ts 的 TaskAsset 一一对应。
/// </summary>
public class TaskAsset
{
    public string Id { get; set; } = string.Empty;

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

    public TaskAsset Clone() => new()
    {
        Id = Id,
        Type = Type,
        Url = Url,
        ThumbnailUrl = ThumbnailUrl,
        FileName = FileName,
        FileSize = FileSize,
        Width = Width,
        Height = Height,
        Duration = Duration,
    };
}
