using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WooVisAgent.Api.Tests;

/// <summary>各测试类共用的请求辅助方法</summary>
internal static class AuthTestHelper
{
    public static async Task<AuthResult> RegisterAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(AuthEndpoints.Register, new RegisterRequest(email, password));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(result);
        return result!;
    }

    public static async Task<string> FetchEmailCodeAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(AuthEndpoints.EmailCode, new SendEmailCodeRequest(email));
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmailCodePayload>();
        Assert.NotNull(payload?.Code);
        return payload!.Code!;
    }

    /// <summary>读取失败响应的 detail 文案（ProblemDetails.Detail）</summary>
    public static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("detail", out var detail))
            return detail.GetString() ?? string.Empty;

        // 模型校验失败时返回 ValidationProblemDetails，明细在 errors 里
        if (document.RootElement.TryGetProperty("errors", out var errors))
            return errors.ToString();

        return json;
    }

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    public static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
