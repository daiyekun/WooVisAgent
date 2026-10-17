using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 会话列表「图片 / 视频」筛选与会话类型归属的回归测试。
///
/// 两个独立的缺陷都表现为「点了分类却还是显示全部」：
/// <list type="number">
///   <item>
///     <b>前端没有把筛选条件传给 store</b>：按钮改的是页面局部 ref，请求里始终没有 <c>type</c>，
///     后端按「无筛选」返回全部。这里从后端接口侧锁住 <c>type=image/video</c> 的过滤语义，
///     前端即使修好了参数，过滤口径也必须成立。
///   </item>
///   <item>
///     <b>会话被默认建成了图片类型</b>：聊天页进入时先要一个占位会话（默认 Image），
///     首个视频任务复用了它却从不改类型，于是视频会话永远归在「图片」下。
///     修好前端参数后，这类数据依旧筛不出来。
///   </item>
/// </list>
/// </summary>
public class ConversationTypeFilterTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string ImageGenerationsUrl = "/api/image/generations";
    private const string VideoGenerationsUrl = "/api/video/generations";
    private const string ConversationsUrl = "/api/conversations";

    [Fact]
    public async Task 按类型筛选_只返回对应类型的会话()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var imageModel = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());
        var videoModel = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.VideoModel());

        // 各提交一次，两条会话的类型分别由各自的首个任务决定
        await SubmitImageAsync(token, imageModel.Id, "一只坐在窗台上的橘猫");
        await SubmitVideoAsync(token, videoModel.Id, "一只戴帽子的猫在跳舞", conversationId: null);

        var all = await ListAsync(token, null);
        var image = await ListAsync(token, "image");
        var video = await ListAsync(token, "video");

        // 不筛选时两种会话都在，否则说明分类本身写错了
        Assert.Contains(all, item => item.Type == "image");
        Assert.Contains(all, item => item.Type == "video");

        Assert.NotEmpty(image);
        Assert.NotEmpty(video);

        // 两个分类不交叉，否则「筛选」就只是装饰
        Assert.All(image, item => Assert.Equal("image", item.Type));
        Assert.All(video, item => Assert.Equal("video", item.Type));
        Assert.Empty(image.Select(x => x.Id).Intersect(video.Select(x => x.Id)));
    }

    [Fact]
    public async Task 视频任务复用占位会话时_会话类型会被纠正为视频()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var videoModel = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.VideoModel());

        // 聊天页的实际行为：进入页面先 ensure 一个空会话（默认图片类型）
        var ensured = await EnsureAsync(token);
        Assert.Equal("image", ensured.Type);

        var submit = await SubmitVideoAsync(token, videoModel.Id, "海边的日落", conversationId: ensured.Id);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var list = await ListAsync(token, "video");
        var conversation = Assert.Single(list);

        // 否则「视频」分类里永远查不到这条会话 —— 与用户看到的现象一致
        Assert.Equal(ensured.Id, conversation.Id);
        Assert.Equal("video", conversation.Type);
    }

    [Fact]
    public async Task 视频任务应计入会话的视频分类计数()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var videoModel = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.VideoModel());

        await SubmitVideoAsync(token, videoModel.Id, "一只在草地上奔跑的狗", conversationId: null);

        var summary = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, $"{ConversationsUrl}/summary", token);

        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var counts = await summary.Content.ReadFromJsonAsync<SummaryResult>(WebJson);
        Assert.NotNull(counts);

        // 分类页签上的「视频(N)」就是这个计数，它必须与实际筛选结果一致
        Assert.Equal(1, counts!.VideoCount);
        Assert.Equal(0, counts.ImageCount);
        Assert.Equal(1, counts.Total);
    }

    // ---- 辅助 ----------------------------------------------------------

    private async Task<HttpResponseMessage> SubmitImageAsync(
        string token, string modelId, string prompt, string? conversationId = null)
    {
        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, ImageGenerationsUrl, token,
            new
            {
                modelId,
                prompt,
                conversationId,
                width = 1024,
                height = 1024,
                batchSize = 1,
                steps = 20,
                cfgScale = 7.0,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private async Task<HttpResponseMessage> SubmitVideoAsync(
        string token, string modelId, string prompt, string? conversationId)
    {
        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, VideoGenerationsUrl, token,
            new
            {
                modelId,
                prompt,
                conversationId,
                durationSeconds = 5,
                resolution = "720P",
            });

        return response;
    }

    private async Task<ConversationItem> EnsureAsync(string token)
    {
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, $"{ConversationsUrl}/ensure", token, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var conversation = await response.Content.ReadFromJsonAsync<ConversationItem>(WebJson);
        Assert.NotNull(conversation);
        return conversation!;
    }

    private async Task<List<ConversationItem>> ListAsync(string token, string? type)
    {
        var url = type is null
            ? $"{ConversationsUrl}?page=1&pageSize=50"
            : $"{ConversationsUrl}?page=1&pageSize=50&type={type}";

        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResult>(WebJson);
        Assert.NotNull(paged);
        return paged!.Items;
    }

    private sealed record PagedResult
    {
        public List<ConversationItem> Items { get; init; } = [];
        public int Total { get; init; }
    }

    private sealed record ConversationItem
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Type { get; init; } = "";
        public int MessageCount { get; init; }
        public int TaskCount { get; init; }
    }

    private sealed record SummaryResult(int Total, int ImageCount, int VideoCount);
}
