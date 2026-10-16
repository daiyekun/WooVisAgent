using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WooVisAgentServices.IdentityService.Options;

namespace WooVisAgentServices.IdentityService.Contracts;

public interface IEmailCodeService
{
    /// <summary>生成验证码并下发（当前未接入邮件通道，验证码只写日志）</summary>
    Task<EmailCodeResult> SendAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>校验验证码，校验通过后立即失效</summary>
    bool Validate(string email, string? code);
}

public record EmailCodeResult(bool Success, string? Code, int ExpireSeconds, string? Error);

/// <summary>
/// 进程内验证码存储。
/// 未接入 Redis / SMTP，适用于单实例开发环境；多实例部署时需替换为分布式实现。
/// </summary>
public class InMemoryEmailCodeService(
    IdentityServiceOptions options,
    ILogger<InMemoryEmailCodeService> logger) : IEmailCodeService
{
    private static readonly ConcurrentDictionary<string, Entry> Codes = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string Code, DateTime ExpiresAt);

    public Task<EmailCodeResult> SendAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Task.FromResult(new EmailCodeResult(false, null, 0, "邮箱不能为空。"));

        cancellationToken.ThrowIfCancellationRequested();

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expireSeconds = options.EmailCodeExpireSeconds <= 0 ? 300 : options.EmailCodeExpireSeconds;

        Codes[email] = new Entry(code, DateTime.UtcNow.AddSeconds(expireSeconds));

        logger.LogInformation("邮箱 {Email} 的验证码为 {Code}（{Seconds} 秒内有效，当前未接入邮件通道）",
            email, code, expireSeconds);

        return Task.FromResult(new EmailCodeResult(true, code, expireSeconds, null));
    }

    public bool Validate(string email, string? code)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return false;

        if (!Codes.TryGetValue(email, out var entry))
            return false;

        if (entry.ExpiresAt < DateTime.UtcNow)
        {
            Codes.TryRemove(email, out _);
            return false;
        }

        if (!string.Equals(entry.Code, code.Trim(), StringComparison.Ordinal))
            return false;

        Codes.TryRemove(email, out _);
        return true;
    }
}
