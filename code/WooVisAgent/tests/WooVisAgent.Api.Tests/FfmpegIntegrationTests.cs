using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WooVisAgentServices.VideoService.Support;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 用<b>真实 ffmpeg</b>验证封面抽取与元信息读取。
///
/// 为什么要单独一套：前面那些测试都基于"假 exe"，
/// 它们能锁住配置解析与降级逻辑，但证明不了
/// 「-ss / -frames:v 这组参数真的能抽出第 1 秒的帧」「ffprobe 真的读得出时长」。
/// 参数写错（比如把 -ss 放在 -i 后面导致每次都从头解码）在假 exe 上完全测不出来，
/// 却会让线上<b>每条视频都慢十倍</b>。
///
/// <para>
/// 运行前置：环境变量 <c>VIDEO_MEDIA_TEST_FFMPEG</c> 指向真实的 ffmpeg 可执行文件。
/// 未设置时全部跳过——这属于"依赖外部工具"的用例，不该让没有 ffmpeg 的机器红灯。
/// </para>
/// </summary>
public class FfmpegIntegrationTests
{
    private static string? ResolveFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("VIDEO_MEDIA_TEST_FFMPEG");

        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static FfmpegVideoMediaInspector CreateInspector(string ffmpegPath)
        => new(
            Options.Create(new VideoMediaOptions
            {
                FfmpegPath = ffmpegPath,
                TimeoutSeconds = 60,
                ThumbnailAtSecond = 1,
            }),
            NullLogger<FfmpegVideoMediaInspector>.Instance);

    [Fact]
    public async Task 真实ffmpeg应能读出时长与分辨率()
    {
        var ffmpeg = ResolveFfmpeg();

        if (ffmpeg is null)
            return;

        var video = Path.Combine(Path.GetTempPath(), "woovideo-int", "sample.mp4");

        if (!File.Exists(video))
            return;

        var inspector = CreateInspector(ffmpeg);
        var info = await inspector.ProbeAsync(video);

        Assert.NotNull(info);
        Assert.NotNull(info!.DurationSeconds);
        Assert.True(info.DurationSeconds > 0, $"时长应为正数，实际 {info.DurationSeconds}");
        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
    }

    [Fact]
    public async Task 真实ffmpeg应能抽出封面图片()
    {
        var ffmpeg = ResolveFfmpeg();

        if (ffmpeg is null)
            return;

        var video = Path.Combine(Path.GetTempPath(), "woovideo-int", "sample.mp4");

        if (!File.Exists(video))
            return;

        var target = Path.Combine(Path.GetTempPath(), "woovideo-int", "cover.jpg");
        var inspector = CreateInspector(ffmpeg);

        var result = await inspector.ExtractThumbnailAsync(video, target);

        Assert.NotNull(result);
        Assert.True(File.Exists(result!), $"封面文件应已生成：{result}");

        // 封面必须是有效图片（jpg 头是 FF D8 FF），而不是 0 字节的空文件
        var bytes = await File.ReadAllBytesAsync(result!);

        Assert.True(bytes.Length > 100, $"封面文件过小，可能抽帧失败：{bytes.Length} 字节");
        Assert.True(
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "封面应为有效 JPEG（FF D8 FF 头）");
    }
}