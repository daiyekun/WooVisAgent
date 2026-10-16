using System.Net;
using System.Net.Http.Json;

namespace WooVisAgent.Api.Tests;

/// <summary>注册用例（RegisterUserCommand）集成测试</summary>
public class RegisterApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    [Fact]
    public async Task 合法邮箱与密码_注册成功并直接返回登录态()
    {
        var email = AuthEndpoints.UniqueEmail("register-ok");

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.UserId));
        Assert.Equal(email, result.UserName);
        Assert.Equal(email, result.Email);
        Assert.Equal("User", result.Role);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        Assert.True(result.ExpiresAt > DateTime.UtcNow.AddMinutes(-1), "过期时间应晚于当前时间");
    }

    [Fact]
    public async Task 重复注册同一邮箱_返回400并提示已注册()
    {
        var email = AuthEndpoints.UniqueEmail("register-dup");
        await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("已注册", detail);
    }

    [Fact]
    public async Task 密码不足8位_返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(AuthEndpoints.UniqueEmail("register-short"), "123"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("密码", detail);
    }

    [Fact]
    public async Task 密码不含数字_被Identity密码策略拒绝并返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(AuthEndpoints.UniqueEmail("register-nodigit"), "abcdefghij"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    [InlineData("@no-local-part.com")]
    public async Task 邮箱格式非法_返回400(string email)
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new RegisterRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 请求体缺少字段_返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Register,
            new { email = AuthEndpoints.UniqueEmail("register-empty") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 注册后可直接用该账号登录()
    {
        var email = AuthEndpoints.UniqueEmail("register-then-login");
        await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Login,
            new LoginRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(result);
        Assert.Equal(email, result!.Email);
    }
}
