namespace WooVisAgent.Api.Tests;

/// <summary>接口路径与测试数据工具</summary>
public static class AuthEndpoints
{
    public const string Register = "/api/auth/register";
    public const string Login = "/api/auth/login";
    public const string EmailCode = "/api/auth/email-code";
    public const string Me = "/api/auth/me";

    /// <summary>合法密码：8 位以上、字母 + 数字</summary>
    public const string ValidPassword = "abcd1234";

    public static string UniqueEmail(string prefix = "user")
        => $"{prefix}-{Guid.NewGuid():N}@example.com";
}

public record RegisterRequest(string Email, string Password, string? EmailCode = null);

public record LoginRequest(string Account, string Password);

public record SendEmailCodeRequest(string Email);

public record AuthResult(
    string UserId,
    string UserName,
    string? Email,
    string Role,
    string Token,
    DateTime ExpiresAt);

public record UserProfileResult(string UserId, string UserName, string? Email, string Role);

public record EmailCodePayload(string Email, int ExpireSeconds, string? Code);
