using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgentServices.IdentityService.Queries;

public record UserProfileDto(string UserId, string UserName, string? Email, string Role);

/// <summary>读取当前登录用户资料，供前端刷新页面后恢复会话使用</summary>
public record GetUserProfileQuery : IQuery<Result<UserProfileDto>>;

public class GetUserProfileQueryHandler(
    ICurrentUser currentUser,
    UserManager<IdentityUser> userManager)
    : IQueryHandler<GetUserProfileQuery, Result<UserProfileDto>>
{
    public async Task<Result<UserProfileDto>> Handle(GetUserProfileQuery query, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var user = await userManager.FindByIdAsync(currentUser.Id);
        if (user == null)
            return Result.NotFound("用户不存在。");

        var roles = await userManager.GetRolesAsync(user);

        return Result.Success(new UserProfileDto(
            user.Id,
            user.UserName!,
            user.Email,
            roles.FirstOrDefault() ?? string.Empty));
    }
}
