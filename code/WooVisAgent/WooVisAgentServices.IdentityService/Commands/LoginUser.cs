using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Diagnostics;
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
        var account = command.Account?.Trim() ?? string.Empty;

        // 登录链路单独开一个 span。管道里的通用 span 只知道"执行了 LoginUserCommand"，
        // 这里补上排查登录问题真正需要的三个信息：
        //   1. 走的是邮箱还是用户名（决定查哪张表，账号不存在时最先怀疑这里）；
        //   2. 卡在哪一步——用户没找到 / 密码不对 / 签发 Token 失败；
        //   3. 账号本身，便于按人筛选出「某个人今天所有登录记录」。
        // 注意：绝不记录密码，连哈希和长度都不记。
        using var activity = TraceSources.ApiSource.StartActivity("auth.login");
        activity?.SetTag("auth.account", account);
        activity?.SetTag("auth.lookup_by", account.Contains('@') ? "email" : "username");

        // 1. 查找用户：带 @ 的按邮箱查，否则按用户名查
        var user = account.Contains('@')
            ? await userManager.FindByEmailAsync(account)
            : await userManager.FindByNameAsync(account);

        if (user == null)
        {
            activity?.SetTag("auth.result", "user_not_found");
            activity.RecordSuccess();
            return Result.Unauthorized("账号或密码无效。");
        }

        activity?.SetTag("auth.user_id", user.Id);
        activity?.SetTag("auth.user_name", user.UserName);

        // 2. 校验密码
        if (!await userManager.CheckPasswordAsync(user, command.Password ?? string.Empty))
        {
            activity?.SetTag("auth.result", "bad_password");
            activity.RecordSuccess();

            // 密码错误属于「正常业务流程里的失败」，不是异常。
            // 显式记 Ok 而不是 Error，避免面板上一堆红色让人误以为系统报错。
            return Result.Unauthorized("账号或密码无效。");
        }

        // 3. 生成 Token
        var authToken = await tokenGenerator.GenerateTokenAsync(user, cancellationToken);
        var roles = await userManager.GetRolesAsync(user);

        activity?.SetTag("auth.result", "success");
        activity?.SetTag("auth.role", roles.FirstOrDefault() ?? string.Empty);
        activity?.SetTag("auth.token_expires_at", authToken.ExpiresAt);
        activity.RecordSuccess();

        return Result.Success(new LoginUserDto(
            user.Id,
            user.UserName!,
            user.Email,
            roles.FirstOrDefault() ?? string.Empty,
            authToken.Token,
            authToken.ExpiresAt));
    }
}
