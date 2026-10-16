using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Identity;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.IdentityService.Contracts;
using WooVisAgentServices.IdentityService.Options;

namespace WooVisAgentServices.IdentityService.Commands;

/// <summary>
/// 自注册用例：邮箱 + 密码（可选邮箱验证码），注册成功即签发 Token，前端可直接进入控制台。
/// </summary>
public record RegisterUserCommand(string Email, string Password, string? EmailCode = null)
    : ICommand<Result<LoginUserDto>>;

public class RegisterUserCommandHandler(
    UserManager<IdentityUser> userManager,
    ISender sender,
    IJwtTokenGenerator tokenGenerator,
    IEmailCodeService emailCodeService,
    IdentityServiceOptions identityOptions) : ICommandHandler<RegisterUserCommand, Result<LoginUserDto>>
{
    public async Task<Result<LoginUserDto>> Handle(RegisterUserCommand command, CancellationToken cancellationToken)
    {
        var email = command.Email?.Trim() ?? string.Empty;

        // 1. 邮箱格式校验
        if (!IsValidEmail(email))
            return Result.Invalid("请输入有效的邮箱地址。");

        // 2. 密码强度校验（与 Identity 的 PasswordOptions 保持一致）
        if (string.IsNullOrWhiteSpace(command.Password) || command.Password.Length < 8)
            return Result.Invalid("密码长度至少 8 位。");

        // 3. 邮箱验证码校验（按配置开关）
        if (identityOptions.RequireEmailCode && !emailCodeService.Validate(email, command.EmailCode))
            return Result.Invalid("邮箱验证码不正确或已过期。");

        // 4. 邮箱唯一性校验
        if (await userManager.FindByEmailAsync(email) != null)
            return Result.Invalid("该邮箱已注册，请直接登录。");

        // 5. 创建用户（复用 CreateUserCommand，含默认角色分配）
        var created = await sender.Send(new CreateUserCommand(email, command.Password, email), cancellationToken);
        if (!created.IsSuccess)
            return Result.From(created);

        // 6. 注册即登录
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
            return Result.Failure("注册成功但用户读取失败，请稍后重试。");

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

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256)
            return false;

        try
        {
            var address = new MailAddress(email);
            return string.Equals(address.Address, email, System.StringComparison.OrdinalIgnoreCase);
        }
        catch (System.FormatException)
        {
            return false;
        }
    }
}
