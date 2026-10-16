using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgentServices.IdentityService.Commands;

public record CreatedUserDto(string Id, string UserName);

public record CreateUserCommand(string UserName, string Password) : ICommand<Result<CreatedUserDto>>;

public class CreateUserCommandHandler(
    UserManager<IdentityUser> userManager) : ICommandHandler<CreateUserCommand, Result<CreatedUserDto>>
{
    public async Task<Result<CreatedUserDto>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var user = new IdentityUser
        {
            UserName = command.UserName
        };

        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded)
            return Result.Failure(result.Errors);

        // 默认分配角色 
        await userManager.AddToRoleAsync(user, "User");

        return Result.Success(new CreatedUserDto(user.Id, user.UserName));
    }
}