using Microsoft.AspNetCore.Mvc;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgent.Api.Extensions;

/// <summary>
/// 把用例层的 Result 翻译成 HTTP 响应。
/// 失败时统一返回 ProblemDetails，字段结构与前端 src/api/http.ts 的解析逻辑对齐
/// （detail 为可直接展示的文案，errors 为明细列表）。
/// </summary>
public static class ResultExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return new OkObjectResult(result.Value);

        var messages = result.Errors?
            .Select(error => error?.ToString() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray() ?? [];

        var (statusCode, title) = result.Status switch
        {
            ResultStatus.Unauthorized => (StatusCodes.Status401Unauthorized, "未认证或登录状态已失效"),
            ResultStatus.Forbidden => (StatusCodes.Status403Forbidden, "没有访问该资源的权限"),
            ResultStatus.NotFound => (StatusCodes.Status404NotFound, "资源不存在"),
            ResultStatus.Invalid => (StatusCodes.Status400BadRequest, "请求参数有误"),
            _ => (StatusCodes.Status400BadRequest, "请求处理失败"),
        };

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = messages.Length > 0 ? string.Join("；", messages) : title,
        };
        problem.Extensions["errors"] = messages;

        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
