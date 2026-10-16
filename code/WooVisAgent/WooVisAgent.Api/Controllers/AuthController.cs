using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.IdentityService.Commands;
using WooVisAgentServices.IdentityService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 注册 / 登录 / 会话接口，对应用例见 WooVisAgentServices.IdentityService。
/// 前端调用方：WooVisAgent.Web/src/api/auth.ts
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(ISender sender) : ControllerBase
{
    /// <summary>注册：邮箱 + 密码（可选邮箱验证码），成功即签发 Token</summary>
    [HttpPost("register")]
    [ProducesResponseType<LoginUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RegisterUserCommand(request.Email, request.Password, request.EmailCode),
            cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>登录：账号支持邮箱或用户名</summary>
    [HttpPost("login")]
    [ProducesResponseType<LoginUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new LoginUserCommand(request.Account, request.Password),
            cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>发送邮箱验证码。开发环境未接入邮件通道时，验证码会直接回传（见 Identity:ExposeEmailCodeInResponse）</summary>
    [HttpPost("email-code")]
    [ProducesResponseType<EmailCodeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SendEmailCode([FromBody] SendEmailCodeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SendEmailCodeCommand(request.Email), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>读取当前登录用户资料，前端刷新页面后用它恢复会话</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetUserProfileQuery(), cancellationToken);
        return result.ToActionResult();
    }

    public record RegisterRequest
    {
        [Required(ErrorMessage = "邮箱不能为空。")]
        [EmailAddress(ErrorMessage = "请输入有效的邮箱地址。")]
        public string Email { get; init; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空。")]
        [MinLength(8, ErrorMessage = "密码长度至少 8 位。")]
        public string Password { get; init; } = string.Empty;

        /// <summary>邮箱验证码，Identity:RequireEmailCode 打开时必填</summary>
        public string? EmailCode { get; init; }
    }

    public record LoginRequest
    {
        [Required(ErrorMessage = "账号不能为空。")]
        public string Account { get; init; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空。")]
        public string Password { get; init; } = string.Empty;
    }

    public record SendEmailCodeRequest
    {
        [Required(ErrorMessage = "邮箱不能为空。")]
        [EmailAddress(ErrorMessage = "请输入有效的邮箱地址。")]
        public string Email { get; init; } = string.Empty;
    }
}
