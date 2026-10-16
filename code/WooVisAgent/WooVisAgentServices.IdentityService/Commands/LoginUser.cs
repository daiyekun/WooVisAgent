using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.IdentityService.Contracts;


namespace WooVisAgentServices.IdentityService.Commands;

public record LoginUserDto(string UserName, string Token);

public record LoginUserCommand(string UserName, string Password) : ICommand<Result<LoginUserDto>>;

public class LoginUserCommandHandler(
    UserManager<IdentityUser> userManager,
    IJwtTokenGenerator tokenGenerator)
    : ICommandHandler<LoginUserCommand, Result<LoginUserDto>>
{
    public async Task<Result<LoginUserDto>> Handle(LoginUserCommand command, CancellationToken cancellationToken)
    {
        // 1. 查找用户
        var user = await userManager.FindByNameAsync(command.UserName);
        if (user == null) return Result.Unauthorized("用户名或密码无效。");

        // 2. 验证密码
        var result = await userManager.CheckPasswordAsync(user, command.Password);
        if (!result) return Result.Unauthorized("用户名或密码无效。");

        // 3. 登录成功，生成 Token
        var token = await tokenGenerator.GenerateTokenAsync(user);

        // 4. 返回结果
        return Result.Success(new LoginUserDto(command.UserName, token));
    }
}