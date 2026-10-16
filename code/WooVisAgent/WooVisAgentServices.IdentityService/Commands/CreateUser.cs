using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.IdentityService.Options;

namespace WooVisAgentServices.IdentityService.Commands;

public record CreatedUserDto(string Id, string UserName, string? Email);

public record CreateUserCommand(string UserName, string Password, string? Email = null)
    : ICommand<Result<CreatedUserDto>>;

public class CreateUserCommandHandler(
    UserManager<IdentityUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IdentityServiceOptions identityOptions) : ICommandHandler<CreateUserCommand, Result<CreatedUserDto>>
{
    public async Task<Result<CreatedUserDto>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var user = new IdentityUser
        {
            UserName = command.UserName,
            Email = command.Email
        };

        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded)
            return Result.Failure(result.Errors.Select(e => e.Description).ToArray<object>());

        // 默认分配角色（角色不存在时先补建，避免种子数据缺失导致注册失败）
        var roleName = string.IsNullOrWhiteSpace(identityOptions.DefaultRole) ? "User" : identityOptions.DefaultRole;
        if (!await roleManager.RoleExistsAsync(roleName))
            await roleManager.CreateAsync(new IdentityRole(roleName));

        await userManager.AddToRoleAsync(user, roleName);

        return Result.Success(new CreatedUserDto(user.Id, user.UserName!, user.Email));
    }
}
