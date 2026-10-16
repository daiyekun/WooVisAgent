using System.Net;
using System.Net.Http.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 开启 Identity:RequireEmailCode 后的注册链路：验证码必填、错误验证码被拒、验证码一次性。
/// </summary>
public class EmailCodeRegisterTests(EmailCodeRequiredFactory factory)
    : IClassFixture<EmailCodeRequiredFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    [Fact]
    public async Task 发送验证码_返回6位数字()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.EmailCode,
            new SendEmailCodeRequest(AuthEndpoints.UniqueEmail("code-send")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<EmailCodePayload>();
        Assert.NotNull(payload);
        Assert.Matches("^[0-9]{6}$", payload!.Code ?? string.Empty);
        Assert.True(payload.ExpireSeconds > 0);
    }

    [Fact]
    public async Task 邮箱格式非法_发送验证码返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.EmailCode,
            new SendEmailCodeRequest("bad-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 未提供验证码_注册返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(AuthEndpoints.UniqueEmail("code-missing"), AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("验证码", detail);
    }

    [Fact]
    public async Task 验证码错误_注册返回400()
    {
        var email = AuthEndpoints.UniqueEmail("code-wrong");
        var realCode = await AuthTestHelper.FetchEmailCodeAsync(_client, email);
        var wrongCode = realCode == "000000" ? "111111" : "000000";

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(email, AuthEndpoints.ValidPassword, wrongCode));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("验证码", detail);
    }

    [Fact]
    public async Task 验证码正确_注册成功()
    {
        var email = AuthEndpoints.UniqueEmail("code-ok");
        var code = await AuthTestHelper.FetchEmailCodeAsync(_client, email);

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(email, AuthEndpoints.ValidPassword, code));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Token));
    }

    [Fact]
    public async Task 验证码一次性_同一验证码不能二次使用()
    {
        var firstEmail = AuthEndpoints.UniqueEmail("code-reuse-1");
        var code = await AuthTestHelper.FetchEmailCodeAsync(_client, firstEmail);

        var first = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(firstEmail, AuthEndpoints.ValidPassword, code));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var secondEmail = AuthEndpoints.UniqueEmail("code-reuse-2");
        var second = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(secondEmail, AuthEndpoints.ValidPassword, code));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }
}
