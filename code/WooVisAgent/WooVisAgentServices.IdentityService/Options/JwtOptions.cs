
using System.Text;
using Microsoft.Extensions.Configuration;

namespace WooVisAgentServices.IdentityService.Options;

/// <summary>
/// JWT 签发与校验参数，对应 appsettings.json 的 Jwt 节点。
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>签发方</summary>
    public string Issuer { get; set; } = "WooVisAgent";

    /// <summary>接收方</summary>
    public string Audience { get; set; } = "WooVisAgent";

    /// <summary>对称签名密钥，长度至少 32 字节（HmacSha256 要求）</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Token 有效期（分钟）</summary>
    public int ExpireMinutes { get; set; } = 120;

    /// <summary>从配置读取，缺失项使用默认值</summary>
    public static JwtOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Issuer = configuration[$"{SectionName}:Issuer"] ?? "WooVisAgent",
        Audience = configuration[$"{SectionName}:Audience"] ?? "WooVisAgent",
        SigningKey = configuration[$"{SectionName}:SigningKey"] ?? string.Empty,
        ExpireMinutes = int.TryParse(configuration[$"{SectionName}:ExpireMinutes"], out var minutes) ? minutes : 120,
    };

    /// <summary>
    /// 取签名密钥字节。HmacSha256 要求不短于 256 bit，过短时补齐，
    /// 保证签发端与校验端使用同一份密钥。
    /// </summary>
    public byte[] GetSigningKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
            throw new InvalidOperationException("未配置 Jwt:SigningKey，无法签发或校验 Token。");

        var key = SigningKey;
        while (Encoding.UTF8.GetByteCount(key) < 32)
            key += "0";

        return Encoding.UTF8.GetBytes(key);
    }
}

