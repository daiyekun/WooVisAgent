using WooVisAgent.Services.Common.Storage;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 产物地址拼接的回归测试。
///
/// 背景：存储层返回的是 <c>uploads/2026/10/18/x.mp4</c> 这种相对路径。
/// 视频侧早期直接把它写进了任务产物和作品库，图片侧则加了前导斜杠。
/// 相对地址会被浏览器按「当前页面路由」解析，于是同一条视频在 <c>/chat</c>
/// 能播、在 <c>/works</c> 却只出一块黑——排查时极容易被当成播放器坏了。
/// </summary>
public class StorageUrlTests
{
    [Theory]
    [InlineData("uploads/2026/10/18/x.mp4", "/uploads/2026/10/18/x.mp4")]
    [InlineData("/uploads/2026/10/18/x.mp4", "/uploads/2026/10/18/x.mp4")]
    [InlineData("uploads\\2026\\10\\18\\x.mp4", "/uploads/2026/10/18/x.mp4")]
    [InlineData("https://cdn.example.com/a.mp4", "https://cdn.example.com/a.mp4")]
    public void 存储相对路径必须转成带前导斜杠的对外地址(string input, string expected)
    {
        Assert.Equal(expected, LocalStorageDefaults.ToPublicUrl(input));
    }

    [Fact]
    public void 空地址不产生只有一个斜杠的脏值()
    {
        Assert.Equal(string.Empty, LocalStorageDefaults.ToPublicUrl(null));
        Assert.Equal(string.Empty, LocalStorageDefaults.ToPublicUrl("   "));
    }
}
