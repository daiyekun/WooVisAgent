namespace WooVisAgent.Services.Common.Contracts;

/// <summary>
/// 后台进程（没有 HTTP 请求上下文）使用的 <see cref="ICurrentUser" /> 实现。
///
/// 存在的原因：<see cref="ICurrentUser" /> 被大量用例依赖，而
/// WooVisAgent.GenerateWorker 复用同一批用例程序集却没有 HTTP 上下文。
/// 以前 Worker 干脆不注册它，开发环境开启「构建时校验服务」后
/// 会直接因为 <c>Unable to resolve service for type ICurrentUser</c> 起不来，
/// 表现为「任务永远排队、页面上一直转圈」——很难一眼看出是宿主没启动。
///
/// 这里刻意返回「未认证」：Worker 只走生成链路，不会碰需要用户身份的用例；
/// 万一将来被误用，得到的也是明确的未授权，而不是静默拿到某个人的数据。
/// </summary>
public sealed class SystemCurrentUser : ICurrentUser
{
    public string? Id => null;

    public string? UserName => "system";

    public string? Role => null;

    public bool IsAuthenticated => false;
}
