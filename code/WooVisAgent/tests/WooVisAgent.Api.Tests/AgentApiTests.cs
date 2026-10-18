using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WooVisAgentServices.AgentService.Services;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// Agent（Microsoft Agent Framework）接入的回归测试。
///
/// 这里**刻意不连真实模型**：用例只覆盖「配了 / 没配 / 未登录」三条分支与地址归一化，
/// 保证 Agent 出问题不会污到登录、图片、视频这些主链路。
/// 真实模型调用放在人工验收与端到端脚本里，不进单测。
/// </summary>
public class AgentApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string StatusUrl = "/api/agent/status";
    private const string PolishUrl = "/api/agent/polish";
    private const string VideoPlanUrl = "/api/agent/video-plan";

    [Fact]
    public async Task 未登录时_Agent接口一律返回401()
    {
        var status = await _client.GetAsync(StatusUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);

        var polish = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, PolishUrl, token: null, new { prompt = "一只猫" });
        Assert.Equal(HttpStatusCode.Unauthorized, polish.StatusCode);
    }

    [Fact]
    public async Task 没有配置语言模型时_状态为不可用且给出可读原因()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var status = await GetStatusAsync(token);

        Assert.True(status.Enabled);
        Assert.False(status.Available);
        Assert.Null(status.ModelName);

        // 文案要能直接告诉用户下一步做什么，而不是只说「失败」
        Assert.NotNull(status.Reason);
        Assert.Contains("语言模型", status.Reason);
    }

    [Fact]
    public async Task 没有配置语言模型时_润色返回可读的400而不是500()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, PolishUrl, token, new { prompt = "一只在窗台上的橘猫，午后阳光" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(WebJson);
        Assert.NotNull(problem);
        Assert.Contains("语言模型", problem!.Detail);
    }

    [Fact]
    public async Task 没有视频模型时_规划接口不会偷偷提交任务()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        // 只配语言模型，一个视频模型都没有
        await ModelTestHelper.CreateAsync(_client, token, LlmModel());

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, VideoPlanUrl, token, new { idea = "一只猫在雨夜街头跳舞" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(WebJson);
        Assert.Contains("视频模型", problem!.Detail);

        // 规划接口只给建议，不该产生任何任务
        var tasks = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, "/api/tasks?page=1&pageSize=10", token);
        Assert.Equal(HttpStatusCode.OK, tasks.StatusCode);
        var paged = await tasks.Content.ReadFromJsonAsync<PagedPayload>(WebJson);
        Assert.NotNull(paged);
        Assert.Equal(0, paged!.Total);
    }

    [Fact]
    public async Task 配好语言模型后_状态变为可用()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, LlmModel("测试语言模型"));

        var status = await GetStatusAsync(token);

        Assert.True(status.Available);
        Assert.Equal("测试语言模型", status.ModelName);
        Assert.Equal("qwen-plus", status.ModelKey);
        Assert.Null(status.Reason);
    }

    [Fact]
    public async Task 停用语言模型后_状态回到不可用()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, LlmModel());

        var disable = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Put, ModelEndpoints.Enabled(created.Id), token, new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var status = await GetStatusAsync(token);
        Assert.False(status.Available);
    }

    [Theory]
    // 只有主机名：必须补 /v1，否则 SDK 会拼成 https://api.deepseek.com/chat/completions → 404
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1")]
    // 已带版本段：原样保留（百炼的兼容层前缀不能被吃掉）
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1")]
    // 尾部斜杠：去掉
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    // 代理前缀：尊重用户填写
    [InlineData("https://gateway.example.com/proxy/v1", "https://gateway.example.com/proxy/v1")]
    public void 接口地址归一化(string input, string expected)
    {
        var uri = EndpointNormalizer.Normalize(input);
        Assert.NotNull(uri);
        Assert.Equal(expected, uri!.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api.openai.com/v1")]
    [InlineData("ftp://example.com")]
    public void 接口地址非法时返回空(string? input)
    {
        Assert.Null(EndpointNormalizer.Normalize(input));
    }

    // ---- 辅助 ----------------------------------------------------------

    private static SaveModelRequest LlmModel(string name = "测试语言模型") => new(
        name,
        "AliyunBailian",
        "llm",
        ModelKey: "qwen-plus",
        Endpoint: "https://dashscope.aliyuncs.com/compatible-mode/v1",
        ApiKey: "sk-test-secret-key",
        Remark: "Agent 集成测试");

    private async Task<AgentStatusPayload> GetStatusAsync(string token)
    {
        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, StatusUrl, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<AgentStatusPayload>(WebJson);
        Assert.NotNull(status);
        return status!;
    }

    private sealed record AgentStatusPayload(
        bool Enabled,
        bool Available,
        string? ModelName,
        string? ModelKey,
        string? Reason);

    private sealed record ProblemPayload(string? Title, string? Detail);

    private sealed record PagedPayload(int Total);
}
