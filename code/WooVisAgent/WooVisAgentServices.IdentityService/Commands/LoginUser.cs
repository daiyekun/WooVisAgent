using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.IdentityService.Contracts;

namespace WooVisAgentServices.IdentityService.Commands;

public record LoginUserDto(
    string UserId,
    string UserName,
    string? Email,
    string Role,
    string Token,
    DateTime ExpiresAt);

/// <summary>Account 支持邮箱或用户名</summary>
public record LoginUserCommand(string Account, string Password) : ICommand<Result<LoginUserDto>>;

public class LoginUserCommandHandler(
    UserManager<IdentityUser> userManager,
    IJwtTokenGenerator tokenGenerator)
    : ICommandHandler<LoginUserCommand, Result<LoginUserDto>>
{
    public async Task<Result<LoginUserDto>> Handle(LoginUserCommand command, CancellationToken cancellationToken)
    {
        // 1. 查找用户：带 @ 的按邮箱查，否则按用户名查
        var account = command.Account?.Trim() ?? string.Empty;
        var user = account.Contains('@')
            ? await userManager.FindByEmailAsync(account)
            : await userManager.FindByNameAsync(account);

        if (user == null)
            return Result.Unauthorized("账号或密码无效。");

        // 2. 校验密码
        if (!await userManager.CheckPasswordAsync(user, command.Password ?? string.Empty))
            return Result.Unauthorized("账号或密码无效。");

        // 3. 生成 Token
        var authToken = await tokenGenerator.GenerateTokenAsync(user, cancellationToken);
        var roles = await userManager.GetRolesAsync(user);

        return Result.Success(new LoginUserDto(
            user.Id,
            user.UserName!,
            user.Email,
            roles.FirstOrDefault() ?? string.Empty,
            authToken.Token,
            authToken.ExpiresAt));
    }
}
