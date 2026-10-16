using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WooVisAgent.Api.Tests;

/// <summary>登录用例（LoginUserCommand + JwtTokenGenerator）集成测试</summary>
public class LoginApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    [Fact]
    public async Task 邮箱加正确密码_登录成功且Token含用户与角色声明()
    {
        var email = AuthEndpoints.UniqueEmail("login-ok");
        var registered = await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Login,
            new LoginRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(result);
        Assert.Equal(registered.UserId, result!.UserId);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));

        var token = new JsonWebTokenHandler().ReadJsonWebToken(result.Token);
        Assert.Equal("WooVisAgent.Tests", token.Issuer);
        Assert.Equal(registered.UserId, token.Subject);
        Assert.Equal(email, token.GetClaim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name").Value);
        Assert.Equal("User", token.GetClaim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Value);
        Assert.True(token.ValidTo > DateTime.UtcNow);
    }

    [Fact]
    public async Task 密码错误_返回401()
    {
        var email = AuthEndpoints.UniqueEmail("login-wrong-pwd");
        await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Login,
            new LoginRequest(email, "wrong-password-1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var detail = await AuthTestHelper.ReadErrorDetailAsync(response);
        Assert.Contains("无效", detail);
    }

    [Fact]
    public async Task 账号不存在_返回401()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Login,
            new LoginRequest(AuthEndpoints.UniqueEmail("login-missing"), AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 账号或密码为空_返回400()
    {
        var response = await _client.PostAsJsonAsync(
            AuthEndpoints.Login,
            new { account = string.Empty, password = string.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 连续两次登录_均可获得可用Token()
    {
        var email = AuthEndpoints.UniqueEmail("login-twice");
        await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var first = await _client.PostAsJsonAsync(AuthEndpoints.Login, new LoginRequest(email, AuthEndpoints.ValidPassword));
        var second = await _client.PostAsJsonAsync(AuthEndpoints.Login, new LoginRequest(email, AuthEndpoints.ValidPassword));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
