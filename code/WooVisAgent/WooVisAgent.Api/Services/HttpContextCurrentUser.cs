using System.Security.Claims;
using WooVisAgent.Services.Common.Contracts;

namespace WooVisAgent.Api.Services;

/// <summary>
/// 从 HttpContext 的 ClaimsPrincipal 中读取当前用户，供用例层（ICurrentUser）使用。
/// 与 JwtBearer 的 TokenValidationParameters（NameClaimType/RoleClaimType）保持一致。
/// </summary>
public class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string? Id => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);

    public string? Role => Principal?.FindFirstValue(ClaimTypes.Role);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
