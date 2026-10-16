using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace WooVisAgent.Api.Tests;

/// <summary>会话接口 /api/auth/me 的鉴权集成测试</summary>
public class SessionApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = AuthTestHelper.CreateClient(factory);

    [Fact]
    public async Task 未携带Token_返回401()
    {
        var response = await _client.GetAsync(AuthEndpoints.Me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token格式非法_返回401()
    {
        var response = await _client.SendAsync(
            AuthTestHelper.Authorized(HttpMethod.Get, AuthEndpoints.Me, "not-a-jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 伪造密钥签发的Token_返回401()
    {
        var forged = CreateTokenWithKey("a-completely-different-signing-key-0123456789");

        var response = await _client.SendAsync(
            AuthTestHelper.Authorized(HttpMethod.Get, AuthEndpoints.Me, forged));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 携带有效Token_返回当前用户资料()
    {
        var email = AuthEndpoints.UniqueEmail("session-me");
        var registered = await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.SendAsync(
            AuthTestHelper.Authorized(HttpMethod.Get, AuthEndpoints.Me, registered.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResult>();
        Assert.NotNull(profile);
        Assert.Equal(registered.UserId, profile!.UserId);
        Assert.Equal(email, profile.UserName);
        Assert.Equal(email, profile.Email);
        Assert.Equal("User", profile.Role);
    }

    [Fact]
    public async Task 注册返回的Token可直接访问受保护接口()
    {
        var email = AuthEndpoints.UniqueEmail("session-register-token");
        var registered = await AuthTestHelper.RegisterAsync(_client, email, AuthEndpoints.ValidPassword);

        var response = await _client.SendAsync(
            AuthTestHelper.Authorized(HttpMethod.Get, AuthEndpoints.Me, registered.Token));

        // 注册即登录：拿注册响应里的 Token 无需再登录即可访问
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string CreateTokenWithKey(string key)
    {
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "WooVisAgent.Tests",
            Audience = "WooVisAgent.Tests",
            Expires = DateTime.UtcNow.AddMinutes(30),
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "forged@example.com"),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256),
        });
    }
}
