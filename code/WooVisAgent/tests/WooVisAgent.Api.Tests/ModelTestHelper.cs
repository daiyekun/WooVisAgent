using System.Net.Http.Json;
using System.Text.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>模型配置测试的请求辅助方法</summary>
internal static class ModelTestHelper
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>注册一个新账号并拿到 Token，保证各用例的数据互不影响</summary>
    public static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var email = AuthEndpoints.UniqueEmail("model");
        var result = await AuthTestHelper.RegisterAsync(client, email, AuthEndpoints.ValidPassword);
        return result.Token;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: WebJson);

        return await client.SendAsync(request);
    }

    public static async Task<ModelResult> CreateAsync(HttpClient client, string token, SaveModelRequest input)
    {
        var response = await SendAsync(client, HttpMethod.Post, ModelEndpoints.Base, token, input);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ModelResult>();
        Assert.NotNull(created);
        return created!;
    }

    public static async Task<List<ModelResult>> ListAsync(HttpClient client, string token, string? category = null)
    {
        var url = category is null ? ModelEndpoints.Base : $"{ModelEndpoints.Base}?category={category}";
        var response = await SendAsync(client, HttpMethod.Get, url, token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<ModelResult>>();
        Assert.NotNull(list);
        return list!;
    }

    /// <summary>一个能提交视频任务的模型配置</summary>
    public static SaveModelRequest VideoModel(string name = "测试文生视频模型") => new(
        name,
        "AliyunBailian",
        "video",
        ModelKey: "wan3.0-video",
        Endpoint: "https://dashscope.aliyuncs.com",
        ApiKey: "sk-test-secret-key",
        Remark: "集成测试创建");

    /// <summary>一个校验一定能通过的图片模型配置</summary>
    public static SaveModelRequest ImageModel(string name = "测试文生图模型") => new(
        name,
        "OpenAI",
        "image",
        ModelKey: "gpt-image-1",
        Endpoint: "https://api.openai.com/v1",
        ApiKey: "sk-test-secret-key",
        Params:
        [
            new ModelParamSpecPayload("size", "尺寸", "select", "1024x1024",
                [new ModelParamOptionPayload("1024x1024", "1024x1024")]),
            new ModelParamSpecPayload("steps", "步数", "number", 20),
        ],
        Remark: "集成测试创建");
}
