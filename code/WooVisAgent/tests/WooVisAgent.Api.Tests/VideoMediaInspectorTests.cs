using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WooVisAgentServices.VideoService.Support;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 视频封面 / 元信息（ffmpeg / ffprobe）的配置与降级行为测试。
///
/// 背景：ffmpeg 是<b>可选</b>依赖——服务器没装时视频必须照常生成成功，
/// 只是没有封面与时长。如果这里抛异常让整条任务失败，
/// 就变成了"为了一个锦上添花的封面，把已经生成好的视频也弄丢了"。
///
/// <para>
/// 这些测试断言的是 <c>IsAvailable</c> 而不只是处理结果。
/// 原因：只看"处理一个不存在的视频文件"的返回值，<b>区分不出</b>
/// 「压根没装 ffmpeg」和「装了但这个文件有问题」——两种情况都返回 null。
/// 而这正是旧实现漏掉的那个缺陷：判定"可用"靠猜，于是配置成默认的
/// <c>ffmpeg</c>、机器上没装时依然被认作可用，
/// 直到每条任务启动进程才失败，最该出现的配置提示反而永远看不到。
/// </para>
/// </summary>
public class VideoMediaInspectorTests
{
    private static FfmpegVideoMediaInspector CreateInspector(string? ffmpegPath, string? ffprobePath = null)
        => new(
            Options.Create(new VideoMediaOptions
            {
                FfmpegPath = ffmpegPath ?? string.Empty,
                FfprobePath = ffprobePath,
                TimeoutSeconds = 5,
            }),
            NullLogger<FfmpegVideoMediaInspector>.Instance);

    /// <summary>造一个能正常退出（退出码 0）的假 ffmpeg</summary>
    private static string CreateExecutable(string name, int exitCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "woovideo-probe", name);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.exe.cmd" : name);

        File.WriteAllText(
            path,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\nexit /b {exitCode}\r\n"
                : $"#!/bin/sh\nexit {exitCode}\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public void 能正常执行的程序应判定为可用()
    {
        var inspector = CreateInspector(CreateExecutable("good", 0));

        Assert.True(inspector.IsAvailable, "退出码为 0 的可执行文件应判定为可用");
    }

    [Fact]
    public void 存在但执行失败的文件应判定为不可用()
    {
        // 这是旧实现漏掉的场景：它只看"路径像不像文件"，
        // 于是把一个跑不起来的文件也认作可用
        var inspector = CreateInspector(CreateExecutable("badexec", 3));

        Assert.False(inspector.IsAvailable, "文件存在但执行失败时必须判定为不可用");
    }

    [Fact]
    public void 路径不存在时应判定为不可用()
    {
        var inspector = CreateInspector(Path.Combine(Path.GetTempPath(), "绝对不存在的目录", "ffmpeg.exe"));

        Assert.False(inspector.IsAvailable);
    }

    [Fact]
    public void 配置为空时应判定为不可用()
    {
        Assert.False(CreateInspector(string.Empty).IsAvailable);
        Assert.False(CreateInspector("   ").IsAvailable);
    }

    [Fact]
    public void 路径带引号时应剥掉引号再判断()
    {
        // 从 PowerShell / 聊天窗口复制出来的路径常带首尾引号，
        // 不剥掉就会被当成文件名的一部分，导致"明明配了却说找不到"
        var real = CreateExecutable("quoted", 0);

        Assert.True(CreateInspector($"\"{real}\"").IsAvailable, "路径外的引号应被忽略");
    }

    [Fact]
    public void 可用性应按实例隔离而不是全局共享()
    {
        // 曾经用 static 缓存探测结论，于是"第一个探测过的配置"
        // 会把结果共享给之后所有实例——换个配置后结论仍是上一个的
        var unavailable = CreateInspector("ffmpeg-definitely-not-installed");
        Assert.False(unavailable.IsAvailable);

        var available = CreateInspector(CreateExecutable("isolated", 0));
        Assert.True(available.IsAvailable, "第二个实例不应被前一个的结论污染");
    }

    [Fact]
    public async Task 不可用时处理视频应安静降级而不是抛异常()
    {
        var inspector = CreateInspector(Path.Combine(Path.GetTempPath(), "不存在的目录", "ffmpeg.exe"));
        var target = Path.Combine(Path.GetTempPath(), "woovideo-degrade", "cover.jpg");

        Assert.Null(await inspector.ExtractThumbnailAsync("任意.mp4", target));
        Assert.Null(await inspector.ProbeAsync("任意.mp4"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task 可用时处理缺失文件应返回空而不是抛异常()
    {
        var inspector = CreateInspector(CreateExecutable("usable", 0));

        // 能跑起来的 ffmpeg，遇到缺失的视频文件时返回 null，
        // 而不是把底层异常抛给调用方
        Assert.Null(await inspector.ProbeAsync(Path.Combine(Path.GetTempPath(), "不存在的视频.mp4")));
    }
}