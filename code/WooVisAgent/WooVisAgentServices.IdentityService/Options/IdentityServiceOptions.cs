
using Microsoft.Extensions.Configuration;

namespace WooVisAgentServices.IdentityService.Options;

/// <summary>
/// 注册相关开关，对应 appsettings.json 的 Identity 节点。
/// 命名加 Service 前缀以区别于 Microsoft.AspNetCore.Identity.IdentityOptions。
/// </summary>
public class IdentityServiceOptions
{
    public const string SectionName = "Identity";

    /// <summary>注册时是否强制校验邮箱验证码</summary>
    public bool RequireEmailCode { get; set; }

    /// <summary>验证码有效期（秒）</summary>
    public int EmailCodeExpireSeconds { get; set; } = 300;

    /// <summary>是否在发送验证码的响应中回传验证码（仅限开发环境，无邮件通道时便于联调）</summary>
    public bool ExposeEmailCodeInResponse { get; set; } = true;

    /// <summary>新注册用户默认角色</summary>
    public string DefaultRole { get; set; } = "User";

    /// <summary>从配置读取，缺失项使用默认值</summary>
    public static IdentityServiceOptions FromConfiguration(IConfiguration configuration) => new()
    {
        RequireEmailCode = bool.TryParse(configuration[$"{SectionName}:RequireEmailCode"], out var require) && require,
        EmailCodeExpireSeconds = int.TryParse(configuration[$"{SectionName}:EmailCodeExpireSeconds"], out var seconds)
            ? seconds
            : 300,
        ExposeEmailCodeInResponse =
            !bool.TryParse(configuration[$"{SectionName}:ExposeEmailCodeInResponse"], out var expose) || expose,
        DefaultRole = configuration[$"{SectionName}:DefaultRole"] ?? "User",
    };
}
