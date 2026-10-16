using System.Threading;
using System.Threading.Tasks;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.IdentityService.Contracts;
using WooVisAgentServices.IdentityService.Options;

namespace WooVisAgentServices.IdentityService.Commands;

/// <summary>Code 仅在开发环境（ExposeEmailCodeInResponse=true）回传，便于无邮件通道时联调</summary>
public record EmailCodeDto(string Email, int ExpireSeconds, string? Code);

public record SendEmailCodeCommand(string Email) : ICommand<Result<EmailCodeDto>>;

public class SendEmailCodeCommandHandler(
    IEmailCodeService emailCodeService,
    IdentityServiceOptions identityOptions) : ICommandHandler<SendEmailCodeCommand, Result<EmailCodeDto>>
{
    public async Task<Result<EmailCodeDto>> Handle(SendEmailCodeCommand command, CancellationToken cancellationToken)
    {
        var result = await emailCodeService.SendAsync(command.Email?.Trim() ?? string.Empty, cancellationToken);
        if (!result.Success)
            return Result.Invalid(result.Error ?? "验证码发送失败。");

        return Result.Success(new EmailCodeDto(
            command.Email!.Trim(),
            result.ExpireSeconds,
            identityOptions.ExposeEmailCodeInResponse ? result.Code : null));
    }
}
