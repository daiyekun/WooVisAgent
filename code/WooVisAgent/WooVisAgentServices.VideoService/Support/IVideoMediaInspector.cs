namespace WooVisAgentServices.VideoService.Support;

/// <summary>
/// 视频的媒体元信息。字段与 <c>TaskAsset</c> 上已有的
/// <c>Duration</c> / <c>Width</c> / <c>Height</c> 一一对应。
/// </summary>
public sealed record VideoMediaInfo(
    double? DurationSeconds,
    int? Width,
    int? Height);

/// <summary>
/// 媒体信息探测与封面抽帧。
///
/// 抽帧依赖 ffmpeg，而 ffmpeg 是<b>可选</b>依赖：
/// 服务器没装时不应该让视频生成整个失败——视频本身已经生成好了，
/// 缺的只是一张封面。因此这里所有方法都以「拿不到就返回 null」为契约，
/// 由调用方决定留空展示。
/// </summary>
public interface IVideoMediaInspector
{
    /// <summary>探测时长与分辨率；ffprobe 不可用时返回空结果</summary>
    Task<VideoMediaInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>抽取第 1 秒（或首个可用帧）为 JPG 封面；ffmpeg 不可用时返回 null</summary>
    Task<string?> ExtractThumbnailAsync(
        string videoPath,
        string thumbnailPath,
        CancellationToken cancellationToken = default);
}