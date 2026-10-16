using System;
using System.ComponentModel;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using WooVisAgentServices.IdentityService.Commands;


namespace WooVisAgentServices.IdentityService.Plugins;

public static class IdentityServiceTools
{
    [DisplayName("CreateUser")]
    [Description("根据提供的用户名和密码创建一个新用户")]
    public static async Task<bool> CreateUserAsync(IServiceProvider sp, 
        [Description("用于注册新用户的用户名")]string userName, 
        [Description("用于注册新用户的密码")]string password)
    {
        var sender = sp.GetRequiredService<ISender>();
        var result = await sender.Send(new CreateUserCommand(userName, password));
        return result.IsSuccess;
    }
}