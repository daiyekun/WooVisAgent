using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 「提交生成但不传 conversationId」的会话落库回归测试。
///
/// 背景：之前 SubmitImageGeneration 在没传会话时<b>不建会话</b>，
/// 于是 task.ConversationId 为 null —— 任务在「任务列表」里查得到，
/// 但它不属于任何会话，侧边栏「最近会话」永远是空的，
/// 重新登录后这段对话也找不回来（用户报告：新建会话没保存住、历史记录看不见）。
///
/// 这组用例锁住修复后的行为：不传会话也必须自动建会话、写消息、更新计数。
/// </summary>
public class ConversationAutocreateTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string GenerationsUrl = "/api/image/generations";
    private const string ConversationsUrl = "/api/conversations";

    [Fact]
    public async Task 不传会话_仍自动创建会话并写入两条消息()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var model = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        // 刻意不传 conversationId —— 这正是前端「新建创作」首次提交的形态
        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, GenerationsUrl, token,
            new
            {
                modelId = model.Id,
                prompt = "一只坐在窗台上的橘猫",
                width = 1024,
                height = 1024,
                batchSize = 1,
                steps = 20,
                cfgScale = 7.0,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 会话列表里必须出现这条会话，且计数正确
        var list = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, $"{ConversationsUrl}?page=1&pageSize=20", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var paged = await list.Content.ReadFromJsonAsync<PagedResult>(WebJson);
        Assert.NotNull(paged);

        Assert.NotEmpty(paged!.Items);

        var conversation = paged.Items.FirstOrDefault(c => c.TaskCount > 0);
        Assert.True(conversation is not null,
            "不传 conversationId 时也必须自动创建会话，否则任务会成为无根之木");

        // 一轮生成 = 用户需求 + 助手结果两条消息
        Assert.Equal(2, conversation!.MessageCount);
        Assert.Equal(1, conversation.TaskCount);

        // 标题用提示词摘要，而不是占位符「新建创作」
        Assert.NotEqual("新建创作", conversation.Title);
    }

    [Fact]
    public async Task 自动创建的会话_重新登录后依然可见()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var model = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var submit = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, GenerationsUrl, token,
            new
            {
                modelId = model.Id,
                prompt = "海边日落",
                width = 1024,
                height = 1024,
                batchSize = 1,
                steps = 20,
                cfgScale = 7.0,
            });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        // 用同一个账号重新登录，模拟用户「重新登录后就看不见了」的场景
        var email = AuthEndpoints.UniqueEmail("conversation-persist");
        var relogin = await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var list = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, $"{ConversationsUrl}?page=1&pageSize=20", token);
        var paged = await list.Content.ReadFromJsonAsync<PagedResult>(WebJson);

        Assert.NotNull(paged);
        Assert.NotEmpty(paged!.Items);

        // 会话按 OwnerId 隔离，另一个账号看不到，这是数据隔离的基本要求
        Assert.All(paged.Items, c => Assert.NotEqual("新建创作", c.Title));
        Assert.NotNull(relogin.Token);
    }

    /// <summary>
    /// 提交响应必须回传 conversationId。
    ///
    /// 背景：会话确实建好了、也落库了，但 ImageGenerationResultDto 里没有这个字段，
    /// 前端拿不到新会话的主键，于是不会把它写回本地状态 —— 会话在列表里"看不见"，
    /// 后续消息也一直走"无会话"分支，重新登录后历史自然对不上。
    /// </summary>
    [Fact]
    public async Task 提交响应回传会话主键_前端才能把消息挂到会话上()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var model = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, GenerationsUrl, token,
            new
            {
                modelId = model.Id,
                prompt = "窗台上的橘猫",
                width = 1024,
                height = 1024,
                batchSize = 1,
                steps = 20,
                cfgScale = 7.0,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var submitted = await response.Content.ReadFromJsonAsync<SubmitResult>(WebJson);
        Assert.NotNull(submitted);

        Assert.False(string.IsNullOrWhiteSpace(submitted!.ConversationId),
            "提交响应必须带上 conversationId，否则前端不知道该把消息挂到哪个会话");

        // 回传的主键必须真的能在详情里查到，否则只是个不存在的 id
        var detail = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, $"{ConversationsUrl}/{submitted.ConversationId}", token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        var detailBody = await detail.Content.ReadFromJsonAsync<DetailResult>(WebJson);
        Assert.NotNull(detailBody);
        Assert.Equal(2, detailBody!.Messages.Count);
    }

    /// <summary>
    /// 取消后进度必须归零。
    ///
    /// 背景：取消用例把状态置为 Canceled，却没有重置 Progress，
    /// 任务停在 progress=100，前端进度条显示满格，和「已取消」自相矛盾。
    /// </summary>
    [Fact]
    public async Task 取消任务后进度归零_不再显示满格进度条()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var model = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Post, GenerationsUrl, token,
            new
            {
                modelId = model.Id,
                prompt = "一只戴帽子的猫",
                width = 1024,
                height = 1024,
                batchSize = 1,
                steps = 20,
                cfgScale = 7.0,
            });

        var submitted = await response.Content.ReadFromJsonAsync<SubmitResult>(WebJson);
        Assert.NotNull(submitted);

        var cancel = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, $"{GenerationsUrl}/{submitted!.TaskId}/cancel", token, new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var canceled = await cancel.Content.ReadFromJsonAsync<TaskItem>(WebJson);
        Assert.NotNull(canceled);
        Assert.Equal("canceled", canceled!.Status);
        Assert.Equal(0, canceled.Progress);
        Assert.Empty(canceled.Outputs);
    }

    // ---- 契约 ----------------------------------------------------------

    private sealed record SubmitResult
    {
        public string TaskId { get; init; } = "";
        public string Status { get; init; } = "";
        public string? ConversationId { get; init; }
    }

    private sealed record TaskItem
    {
        public string Id { get; init; } = "";
        public string Status { get; init; } = "";
        public int Progress { get; init; }
        public List<object> Outputs { get; init; } = [];
    }

    private sealed record DetailResult
    {
        public ConversationItem Conversation { get; init; } = new();
        public List<object> Messages { get; init; } = [];
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
        public int MessageCount { get; init; }
        public int TaskCount { get; init; }
    }
}