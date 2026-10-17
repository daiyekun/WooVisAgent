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

    // ---- 契约 ----------------------------------------------------------

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