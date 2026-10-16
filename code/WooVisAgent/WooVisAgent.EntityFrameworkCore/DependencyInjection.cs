using System.Data;
using System.Text;
using System.Text.RegularExpressions;

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
    /// <para>
    /// 注意 EnsureCreated 只在「整个数据库不存在」时建表，数据库一旦存在（哪怕缺表）就直接返回，
    /// 因此后续版本新增的实体表不会被自动创建；这里额外做一次「缺表补建」兜底。
    /// </para>
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        var dbContext = scope.ServiceProvider.GetRequiredService<AiCopilotDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var createdTables = await EnsureMissingTablesAsync(dbContext, cancellationToken);
        if (createdTables.Count > 0)
        {
            logger.LogInformation("检测到缺失的表，已补建 {Count} 张：{Tables}",
                createdTables.Count, string.Join(", ", createdTables));
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "User" })
        {
            if (await roleManager.RoleExistsAsync(role))
                continue;

            await roleManager.CreateAsync(new IdentityRole(role));
            logger.LogInformation("已创建内置角色 {Role}", role);
        }

        logger.LogInformation("数据库初始化完成（EnsureCreated + 缺表补建 + 角色种子）");
    }

    /// <summary>
    /// 只补建「模型中存在、数据库里没有」的表，不触碰任何已存在的表与数据。
    /// 建表语句直接取自 EF 依据当前模型生成的脚本，保证与实体映射严格一致。
    /// </summary>
    private static async Task<IReadOnlyList<string>> EnsureMissingTablesAsync(
        AiCopilotDbContext dbContext, CancellationToken cancellationToken)
    {
        // InMemory 等非关系型存储没有表结构，跳过
        if (!dbContext.Database.IsRelational())
            return [];

        var modelTables = dbContext.Model.GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(tableName => !string.IsNullOrWhiteSpace(tableName))
            .Select(tableName => tableName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (modelTables.Count == 0)
            return [];

        var existingTables = await GetExistingTableNamesAsync(dbContext, cancellationToken);
        var missingTables = modelTables
            .Where(tableName => !existingTables.Contains(tableName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (missingTables.Count == 0)
            return [];

        // 按脚本原有顺序执行，保证表之间的依赖（外键、索引）顺序正确
        var createScript = dbContext.Database.GenerateCreateScript();
        foreach (var statement in SplitSqlStatements(createScript))
        {
            var targetTable = GetTargetTableName(statement);
            if (targetTable is null || !missingTables.Contains(targetTable))
                continue;

            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        return missingTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>读取当前数据库中已存在的表名。</summary>
    private static async Task<HashSet<string>> GetExistingTableNamesAsync(
        AiCopilotDbContext dbContext, CancellationToken cancellationToken)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM information_schema.tables";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                    tableNames.Add(reader.GetString(0));
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }

        return tableNames;
    }

    /// <summary>
    /// 按分号切分 SQL 脚本，能正确跳过括号内与字符串字面量中的分号。
    /// </summary>
    private static IEnumerable<string> SplitSqlStatements(string script)
    {
        var buffer = new StringBuilder();
        var parenthesisDepth = 0;
        var inStringLiteral = false;

        foreach (var ch in script)
        {
            if (inStringLiteral)
            {
                buffer.Append(ch);
                if (ch == '\'')
                    inStringLiteral = false;

                continue;
            }

            switch (ch)
            {
                case '\'':
                    inStringLiteral = true;
                    buffer.Append(ch);
                    break;
                case '(':
                    parenthesisDepth++;
                    buffer.Append(ch);
                    break;
                case ')':
                    parenthesisDepth--;
                    buffer.Append(ch);
                    break;
                case ';' when parenthesisDepth <= 0:
                    var statement = buffer.ToString().Trim();
                    if (statement.Length > 0)
                        yield return statement;

                    buffer.Clear();
                    break;
                default:
                    buffer.Append(ch);
                    break;
            }
        }

        var rest = buffer.ToString().Trim();
        if (rest.Length > 0)
            yield return rest;
    }

    private static readonly Regex CreateTablePattern = new(
        @"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[^\s(]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreateIndexPattern = new(
        @"^\s*CREATE\s+(?:UNIQUE\s+)?INDEX\s+[^\s]+\s+ON\s+(?<table>[^\s(]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlterTablePattern = new(
        @"^\s*ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?<table>[^\s(]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从单条语句中提取它作用的表名；无法识别时返回 null。</summary>
    private static string? GetTargetTableName(string statement)
    {
        var sql = StripLeadingComments(statement);

        foreach (var pattern in new[] { CreateTablePattern, CreateIndexPattern, AlterTablePattern })
        {
            var match = pattern.Match(sql);
            if (match.Success)
                return NormalizeIdentifier(match.Groups["table"].Value);
        }

        return null;
    }

    private static string StripLeadingComments(string statement)
    {
        var lines = statement.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            break;
        }

        return string.Join('\n', lines.Skip(index));
    }

    /// <summary>去掉 schema 前缀与各种引号，只保留表名本身。</summary>
    private static string NormalizeIdentifier(string identifier)
    {
        var name = identifier.Trim();

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
            name = name[(lastDot + 1)..];

        return name.Trim('"', '\'', '[', ']');
    }
}
