using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using WooVisAgent.Services.Common.Attributes;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Exceptions;


namespace WooVisAgent.Services.Common.Behaviors;

public class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser user) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requiredPermissions = typeof(TRequest)
            .GetCustomAttributes(typeof(AuthorizeRequirementAttribute), true)
            .Cast<AuthorizeRequirementAttribute>()
            .Select(a => a.Permission)
            .ToList();

        if (requiredPermissions.Count == 0)
            return await next(cancellationToken);

        // 1. 用户是否已认证
        if (!user.IsAuthenticated) throw new ForbiddenException("用户未登录");
        // 管理员角色可以访问所有用例
        if (user.Role == "Admin")
            return await next(cancellationToken);

        // 2. 获取角色包含的权限（可以从数据库查询）
        var userPermissions = LoadPermissions(user.Role!);

        // 4. 权限校验
        if (!requiredPermissions.All(p => userPermissions.Contains(p)))
            throw new ForbiddenException("未授权访问");

        return await next(cancellationToken);
    }

    private List<string> LoadPermissions(string role)
    {
        var permissions = new Dictionary<string, List<string>>
        {
            ["User"] = ["AiGateway.CreateSession", "AiGateway.DeleteSession", "AiGateway.GetListSessions"]
        };

        return permissions[role];
    }
}