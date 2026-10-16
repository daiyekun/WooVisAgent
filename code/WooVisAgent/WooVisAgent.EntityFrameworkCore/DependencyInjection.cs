using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.EntityFrameworkCore.Repository;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Repository;


namespace WooVisAgent.EntityFrameworkCore;

public static class DependencyInjection
{
    /// <summary>
    /// 注册 EF Core 上下文、仓储与 ASP.NET Identity 存储。
    /// 默认使用 Npgsql，连接名与 AppHost 中 AddDatabase("woovisagent") 保持一致，
    /// 由 WithReference(postgresdb) 注入 ConnectionStrings:woovisagent。
    /// 置 Database:Provider=InMemory 可切换为内存库（本地无 PostgreSQL 时联调、集成测试使用）。
    /// </summary>
    public static void AddEfCore(this IHostApplicationBuilder builder)
    {
        if (string.Equals(builder.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var databaseName = builder.Configuration["Database:Name"] ?? "woovisagent";
            builder.Services.AddDbContext<AiCopilotDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
        }
        else
        {
            builder.AddNpgsqlDbContext<AiCopilotDbContext>("woovisagent");
        }

        builder.Services.AddScoped(typeof(IReadRepository<>), typeof(EfReadRepository<>));
        builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        builder.Services.AddScoped<IDataQueryService, DataQueryService>();

        builder.Services.AddIdentityCore<IdentityUser>(options =>
            {
                // 与注册页提示保持一致：8 位以上、字母 + 数字
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;

                // 邮箱唯一，注册用例也会提前校验一次
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AiCopilotDbContext>();
    }

    /// <summary>
    /// 首次启动时建表并补齐内置角色。
    /// 当前仓库还没有 EF 迁移，用 EnsureCreated 保证开发环境开箱可用；
    /// 后续引入迁移后应改为 db.Database.MigrateAsync()。
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        var dbContext = scope.ServiceProvider.GetRequiredService<AiCopilotDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "User" })
        {
            if (await roleManager.RoleExistsAsync(role))
                continue;

            await roleManager.CreateAsync(new IdentityRole(role));
            logger.LogInformation("已创建内置角色 {Role}", role);
        }

        logger.LogInformation("数据库初始化完成（EnsureCreated + 角色种子）");
    }
}
