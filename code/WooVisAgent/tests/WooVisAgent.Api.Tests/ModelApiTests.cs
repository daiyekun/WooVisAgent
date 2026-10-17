using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 模型配置接口集成测试（ModelController + WooVisAgentServices.ModelService）。
/// 覆盖：鉴权、增删改查、启用/停用、设为默认、分类筛选、雪花主键、连通性测试。
/// </summary>
public class ModelApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    // ==================== 鉴权 ====================

    [Fact]
    public async Task 未登录_访问模型接口返回401()
    {
        var list = await _client.GetAsync(ModelEndpoints.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        var create = await _client.PostAsJsonAsync(ModelEndpoints.Base, ModelTestHelper.ImageModel());
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    // ==================== 新增与列表 ====================

    [Fact]
    public async Task 新增模型_返回字符串形式的雪花ID并出现在列表中()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        // 64 位雪花 ID：可解析为正的 long，且长度超过 JS 安全整数范围（16 位）
        Assert.True(long.TryParse(created.Id, out var id), $"id 应为 64 位整数，实际为 {created.Id}");
        Assert.True(id > 0);
        Assert.True(created.Id.Length >= 18, $"雪花 ID 位数异常：{created.Id}");
        Assert.True(id > 9007199254740991L, "雪花 ID 应超出 JS 安全整数范围，验证字符串传输的必要性");

        Assert.Equal("测试文生图模型", created.Name);
        Assert.Equal("image", created.Category);
        Assert.True(created.Enabled);
        Assert.False(created.IsDefault);
        Assert.Equal(2, created.Params.Count);

        var list = await ModelTestHelper.ListAsync(_client, token);
        Assert.Single(list);
        Assert.Equal(created.Id, list[0].Id);
    }

    [Fact]
    public async Task 新增模型_ID以JSON字符串返回而非数字()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var response = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, ModelEndpoints.Base, token);
        var json = await response.Content.ReadAsStringAsync();

        // 关键契约：必须是 "id":"123..." 而不是 "id":123...，否则前端会丢精度
        Assert.Contains($"\"id\":\"{created.Id}\"", json);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, document.RootElement[0].GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task 连续新增_ID单调递增且互不相同()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var first = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("模型A"));
        var second = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("模型B"));
        var third = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("模型C"));

        var ids = new[] { first.Id, second.Id, third.Id };
        Assert.Equal(3, ids.Distinct().Count());
        Assert.True(long.Parse(ids[1]) > long.Parse(ids[0]));
        Assert.True(long.Parse(ids[2]) > long.Parse(ids[1]));
    }

    [Fact]
    public async Task 列表_按分类筛选只返回对应分类()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("图片模型"));
        await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("视频模型", "Kling", "video", Endpoint: "https://api.klingai.com/v1", ApiKey: "kling-key"));
        await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("语言模型", "OpenAI", "llm", Endpoint: "https://api.openai.com/v1", ApiKey: "sk-key"));

        var all = await ModelTestHelper.ListAsync(_client, token);
        Assert.Equal(3, all.Count);

        var videos = await ModelTestHelper.ListAsync(_client, token, "video");
        Assert.Single(videos);
        Assert.Equal("视频模型", videos[0].Name);

        var llms = await ModelTestHelper.ListAsync(_client, token, "llm");
        Assert.Single(llms);
        Assert.Equal("llm", llms[0].Category);
    }

    // ==================== 入参校验 ====================

    [Fact]
    public async Task 分类非法_返回400并给出提示()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Base, token,
            new SaveModelRequest("非法分类", "OpenAI", "audio"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("分类", detail);
    }

    [Fact]
    public async Task 名称为空_返回400()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Base, token,
            new SaveModelRequest("   ", "OpenAI", "image"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 接口地址格式非法_返回400()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Base, token,
            new SaveModelRequest("地址非法", "Custom", "image", Endpoint: "ftp://example.com/v1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ==================== 启用 / 停用 ====================

    [Fact]
    public async Task 启用停用_状态可切换()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());
        Assert.True(created.Enabled);

        var disabled = await SendToggleAsync(token, created.Id, false);
        Assert.False(disabled.Enabled);

        var enabled = await SendToggleAsync(token, created.Id, true);
        Assert.True(enabled.Enabled);

        // 列表里也应体现最新状态
        var list = await ModelTestHelper.ListAsync(_client, token);
        Assert.True(list.Single().Enabled);
    }

    [Fact]
    public async Task 停用默认模型_会同时取消默认标记()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token,
            ModelTestHelper.ImageModel() with { IsDefault = true });

        Assert.True(created.IsDefault);

        var disabled = await SendToggleAsync(token, created.Id, false);
        Assert.False(disabled.Enabled);
        Assert.False(disabled.IsDefault);
    }

    // ==================== 默认模型 ====================

    [Fact]
    public async Task 设为默认_同分类内互斥()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var first = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("图片模型A"));
        var second = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel("图片模型B"));
        // 另一个分类不受影响
        var video = await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("视频模型", "Kling", "video",
                Endpoint: "https://api.klingai.com/v1", ApiKey: "kling-key", IsDefault: true));

        var firstIsDefault = await SendSetDefaultAsync(token, first.Id);
        Assert.True(firstIsDefault.IsDefault);

        var secondIsDefault = await SendSetDefaultAsync(token, second.Id);
        Assert.True(secondIsDefault.IsDefault);

        var list = await ModelTestHelper.ListAsync(_client, token);
        Assert.False(list.Single(x => x.Id == first.Id).IsDefault);
        Assert.True(list.Single(x => x.Id == video.Id).IsDefault);
    }

    [Fact]
    public async Task 停用的模型_设为默认返回400()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token,
            ModelTestHelper.ImageModel() with { Enabled = false });

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Default(created.Id), token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("停用", detail);
    }

    // ==================== 更新 / 删除 ====================

    [Fact]
    public async Task 更新模型_字段与参数规格生效()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Put, ModelEndpoints.Detail(created.Id), token,
            new SaveModelRequest(
                "改名后的模型", "AliyunBailian", "video",
                ModelKey: "wanx2.1-i2v-turbo",
                Endpoint: "https://dashscope.aliyuncs.com/api/v1",
                // 密钥留空：应沿用原密钥（接口不回传明文，故这里只验证其余字段与请求成功）
                ApiKey: null,
                Params: [new ModelParamSpecPayload("duration", "时长", "number", 5)],
                Remark: "已更新"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ModelResult>();
        Assert.NotNull(updated);

        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal("改名后的模型", updated.Name);
        Assert.Equal("AliyunBailian", updated.Provider);
        Assert.Equal("video", updated.Category);
        Assert.Equal("wanx2.1-i2v-turbo", updated.ModelKey);
        Assert.Equal("已更新", updated.Remark);
        Assert.Single(updated.Params);
        Assert.Equal("duration", updated.Params[0].Key);
    }

    [Fact]
    public async Task 列表不回传密钥_本人详情回传密钥供编辑回填()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        // 高频列表响应不应扩散任何密钥字段或内容。
        var listResponse = await ModelTestHelper.SendAsync(_client, HttpMethod.Get, ModelEndpoints.Base, token);
        var listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-test-secret-key", listJson);
        Assert.DoesNotContain("apiKey", listJson, StringComparison.OrdinalIgnoreCase);

        // 单条详情已由“登录 + OwnerId”双重隔离，编辑页需要真实值才能正确回填。
        var detailResponse = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, ModelEndpoints.Detail(created.Id), token);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detail = await detailResponse.Content.ReadFromJsonAsync<ModelResult>();
        Assert.NotNull(detail);
        Assert.Equal("sk-test-secret-key", detail!.ApiKey);
        Assert.Equal(created.ModelKey, detail.ModelKey);
    }

    [Fact]
    public async Task 删除模型_列表不再包含该配置()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token, ModelTestHelper.ImageModel());

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Delete, ModelEndpoints.Detail(created.Id), token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Empty(await ModelTestHelper.ListAsync(_client, token));

        // 再查详情应当 404
        var detail = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, ModelEndpoints.Detail(created.Id), token);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task 访问他人模型_返回404()
    {
        var ownerToken = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, ownerToken, ModelTestHelper.ImageModel());

        var otherToken = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var detail = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, ModelEndpoints.Detail(created.Id), otherToken);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var toggle = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Put, ModelEndpoints.Enabled(created.Id), otherToken,
            new ToggleEnabledPayload(false));
        Assert.Equal(HttpStatusCode.NotFound, toggle.StatusCode);

        var remove = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Delete, ModelEndpoints.Detail(created.Id), otherToken);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);

        // 他人列表里看不到
        Assert.Empty(await ModelTestHelper.ListAsync(_client, otherToken));
    }

    // ==================== 连通性测试 ====================

    [Fact]
    public async Task 连通性测试_未配置接口地址时返回失败()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("无地址模型", "Custom", "image", ApiKey: "some-key"));

        var result = await SendTestAsync(token, created.Id);

        Assert.False(result.Success);
        Assert.Null(result.Latency);
        Assert.Contains("接口地址", result.Message);
    }

    [Fact]
    public async Task 连通性测试_云端地址缺少密钥时返回失败()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        var created = await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("无密钥模型", "OpenAI", "image", Endpoint: "https://api.openai.com/v1"));

        var result = await SendTestAsync(token, created.Id);

        Assert.False(result.Success);
        Assert.Contains("API Key", result.Message);
    }

    [Fact]
    public async Task 连通性测试_地址不可达时返回失败并带上耗时()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);
        // 127.0.0.1:1 属于本地地址（无需密钥）且必然连接被拒，结果稳定不依赖外网
        var created = await ModelTestHelper.CreateAsync(_client, token,
            new SaveModelRequest("不可达模型", "ComfyUI", "image", Endpoint: "http://127.0.0.1:1/v1"));

        var result = await SendTestAsync(token, created.Id);

        Assert.False(result.Success);
        Assert.NotNull(result.Latency);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task 连通性测试_草稿模型未填地址时返回失败()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.TestDraft, token,
            new SaveModelRequest("草稿模型", "Custom", "video"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("接口地址", result.Message);
    }

    // ==================== 供应方模型清单 ====================

    [Fact]
    public async Task 供应方模型清单_返回内置清单且未知供应方为空()
    {
        var token = await ModelTestHelper.RegisterAndGetTokenAsync(_client);

        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, ModelEndpoints.ProviderModels("OpenAI"), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var models = await response.Content.ReadFromJsonAsync<List<Dictionary<string, string>>>();
        Assert.NotNull(models);
        Assert.NotEmpty(models!);
        Assert.Contains(models!, item => item["key"] == "gpt-image-1");

        var unknown = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Get, ModelEndpoints.ProviderModels("NotExist"), token);
        var unknownModels = await unknown.Content.ReadFromJsonAsync<List<Dictionary<string, string>>>();
        Assert.NotNull(unknownModels);
        Assert.Empty(unknownModels!);
    }

    // ==================== 辅助 ====================

    private async Task<ModelResult> SendToggleAsync(string token, string id, bool enabled)
    {
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Put, ModelEndpoints.Enabled(id), token,
            new ToggleEnabledPayload(enabled));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ModelResult>();
        Assert.NotNull(result);
        return result!;
    }

    private async Task<ModelResult> SendSetDefaultAsync(string token, string id)
    {
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Default(id), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ModelResult>();
        Assert.NotNull(result);
        return result!;
    }

    private async Task<ModelTestPayload> SendTestAsync(string token, string id)
    {
        var response = await ModelTestHelper.SendAsync(
            _client, HttpMethod.Post, ModelEndpoints.Test(id), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        Assert.NotNull(result);
        return result!;
    }
}
