using System.Diagnostics;
using MediatR;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Diagnostics;

namespace WooVisAgent.Services.Common.Behaviors;

/// <summary>
/// 给每个 MediatR 用例自动套一层 span。
///
/// <para><b>为什么不逐个手写 span</b>：本项目有几十个 Command / Query，
/// 逐个改既容易漏、后续新增用例又会退化成"新的接口没有链路"。
/// 挂在管道行为上，新增用例自动获得 span，一处改动全局生效——
/// 登录（<c>LoginCommand</c>）也就自然出现在链路列表里了。</para>
///
/// <para><b>异常处理</b>：捕获后记录并<b>原样重新抛出</b>。
/// 这里绝不能吞异常——MediatR 管道外层的 <c>Result</c> 转换、
/// Controller 的错误响应、任务失败状态全都依赖异常继续上抛，
/// 在这里吃掉会让接口永远返回 200 空白，或任务卡在「生成中」。</para>
/// </summary>
public class TracingBehavior<TRequest, TResponse>(ICurrentUser currentUser)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // 用请求类型名做 span 名：面板上能直接看出「这条链路是哪个用例」，
        // 比一个笼统的 "HandleRequest" 有用得多。
        var requestName = typeof(TRequest).Name;

        using var activity = TraceSources.ApiSource.StartActivity($"usecase {requestName}");
        activity.TagCurrentUser(currentUser.IsAuthenticated ? currentUser.Id : null);
        activity?.SetTag("wv.use_case", requestName);

        // 登录这类未认证用例拿不到用户 ID，把账号打上去才查得动
        // 「是哪次登录失败了」——只记 enduser.id 的话，登录失败时它是空的。
        if (activity is not null && !currentUser.IsAuthenticated)
        {
            activity.SetTag("wv.request_body", DescribeAnonymousRequest(request));
        }

        try
        {
            var response = await next(cancellationToken);
            activity.RecordSuccess();

            // Result<T> 形式的用例把失败信息藏在返回值里而不是抛异常，
            // 不额外判断的话面板上会显示成成功，排查时极具误导性。
            activity?.SetTag("wv.result_status", DescribeResultStatus(response));

            return response;
        }
        catch (Exception exception)
        {
            activity.RecordFailure(exception);
            throw;
        }
    }

    /// <summary>
    /// 匿名请求（如登录）只提取「账号」这一个字段。
    ///
    /// 刻意不把整个对象序列化进 span：请求里含密码、密钥等敏感字段，
    /// 全量入库既不安全，也会让 span 体积失控。
    /// </summary>
    private static string? DescribeAnonymousRequest(TRequest request)
    {
        var property = request.GetType()
            .GetProperties()
            .FirstOrDefault(p =>
                string.Equals(p.Name, "Account", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "Email", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "UserName", StringComparison.OrdinalIgnoreCase));

        var value = property?.GetValue(request) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// 判断返回的 <c>Result</c> 是否成功，把状态打成一个可筛选的字符串。
    /// 非 Result 返回值统一记 "Ok"（异常已在上面的 catch 里处理过了）。
    /// </summary>
    private static string DescribeResultStatus(TResponse response)
    {
        if (response is null)
        {
            return "Null";
        }

        var isSuccess = response.GetType().GetProperty("IsSuccess")?.GetValue(response);
        return isSuccess is true ? "Ok" : "Failed";
    }
}
