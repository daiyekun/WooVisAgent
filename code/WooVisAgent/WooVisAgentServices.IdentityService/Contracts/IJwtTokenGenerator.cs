using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace WooVisAgentServices.IdentityService.Contracts;

/// <summary>签发结果：Token 本体与过期时间</summary>
public record AuthToken(string Token, DateTime ExpiresAt);

public interface IJwtTokenGenerator
{
    Task<AuthToken> GenerateTokenAsync(IdentityUser user, CancellationToken cancellationToken = default);
}
