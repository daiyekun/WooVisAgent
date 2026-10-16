using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WooVisAgentServices.IdentityService.Contracts;
using WooVisAgentServices.IdentityService.Options;

namespace WooVisAgentServices.IdentityService.Services;

/// <summary>
/// 基于 HmacSha256 对称密钥的 JWT 生成器。
/// 生成的 Token 由 WooVisAgent.Api 的 JwtBearer 中间件用同一组参数校验。
/// </summary>
public class JwtTokenGenerator(
    JwtOptions options,
    UserManager<IdentityUser> userManager,
    ILogger<JwtTokenGenerator> logger) : IJwtTokenGenerator
{
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<AuthToken> GenerateTokenAsync(IdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var roles = await userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var expiresAt = DateTime.UtcNow.AddMinutes(options.ExpireMinutes <= 0 ? 120 : options.ExpireMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(GetSigningKeyBytes()),
                SecurityAlgorithms.HmacSha256),
        };

        var token = Handler.CreateToken(descriptor);
        logger.LogDebug("已为用户 {UserId} 签发 Token，过期时间 {ExpiresAt}", user.Id, expiresAt);

        return new AuthToken(token, expiresAt);
    }

    private byte[] GetSigningKeyBytes() => options.GetSigningKeyBytes();
}
