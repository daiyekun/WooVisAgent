using System.Data;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.EntityFrameworkCore.Diagnostics;
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
        // SQL 追踪拦截器。官方埋点包只有 beta 版（见 EfCoreTracingInterceptor 的说明），
        // 这里自己埋。每条 DbCommand 都会成为一个 span，面板上能看到
        // 「这个接口慢在哪条 SQL 上」——排查接口超时时最缺的就是这一段。
        var sqlTracer = new EfCoreTracingInterceptor(builder.Configuration);

        if (string.Equals(builder.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var databaseName = builder.Configuration["Database:Name"] ?? "woovisagent";
            builder.Services.AddDbContext<AiCopilotDbContext>(options =>
                options.UseInMemoryDatabase(databaseName)
                    .AddInterceptors(sqlTracer));
        }
        else
        {
            builder.AddNpgsqlDbContext<AiCopilotDbContext>("woovisagent", configureDbContextOptions: options =>
                options.AddInterceptors(sqlTracer));
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
    /// <para>
    /// 缺表补建只解决「新表」，解决不了「新列」：给已存在的表加字段时，
    /// EF 生成的 CREATE TABLE 不会被执行（表已存在，不在 missingTables 里），
    /// 而所有查询都会带上这个新列，于是直接报 column does not exist。
    /// 因此这里再补一步「缺列补建」，从模型与库内实际列的差集里生成 ALTER TABLE ADD COLUMN。
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

        var addedColumns = await EnsureMissingColumnsAsync(dbContext, cancellationToken);
        if (addedColumns.Count > 0)
        {
            logger.LogInformation("检测到缺失的列，已补建 {Count} 个：{Columns}",
                addedColumns.Count, string.Join(", ", addedColumns));
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

    /// <summary>
    /// 补建「模型中有、数据库里没有」的列。
    ///
    /// 实现方式：直接从 EF 的关系型模型元数据读取每张表的列定义
    /// （列名、存储类型、可空性、默认值），再与 information_schema 里的实际列做差集，
    /// 只对缺失的列发 ALTER TABLE ... ADD COLUMN。
    /// <para>
    /// 这里刻意不解析 GenerateCreateScript() 的文本：EF 生成的 SQL 里列名带双引号、
    /// 类型带长度括号，按逗号自行切分极易把 <c>varying(256)</c> 误当成一条列定义，
    /// 拼出 <c>ADD COLUMN (256)</c> 这种语法错误的语句。用元数据天然规避这些问题，
    /// 也自动覆盖 EF 的值转换（枚举存成字符串）。
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> EnsureMissingColumnsAsync(
        AiCopilotDbContext dbContext, CancellationToken cancellationToken)
    {
        // InMemory 等非关系型存储没有列结构概念
        if (!dbContext.Database.IsRelational())
            return [];

        var modelColumns = BuildModelColumns(dbContext);
        if (modelColumns.Count == 0)
            return [];

        var existingColumns = await GetExistingColumnsAsync(dbContext, cancellationToken);
        var added = new List<string>();

        foreach (var (table, columns) in modelColumns)
        {
            // 表不存在的情况已由 EnsureMissingTablesAsync 处理，这里不重复建表
            if (!existingColumns.TryGetValue(table, out var actualColumns))
                continue;

            foreach (var (column, definition) in columns)
            {
                if (actualColumns.Contains(column))
                    continue;

                var statement = $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {definition};";
                await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
                added.Add($"{table}.{column}");
            }
        }

        return added;
    }

    /// <summary>
    /// 从 EF 关系型模型元数据里读出「表名 -> (列名 -> 可直接执行的 ADD COLUMN 定义)」。
    /// 只处理我们自己映射的业务表，Identity 的表交给框架自身演进。
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildModelColumns(AiCopilotDbContext dbContext)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName) || !IsBusinessTable(tableName))
                continue;

            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();
                var storeType = property.GetColumnType();

                // 存储类型读不到时跳过，交由后续正式迁移处理
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(storeType))
                    continue;

                columns[columnName] = BuildAddColumnDefinition(property, columnName, storeType);
            }

            if (columns.Count > 0)
                result[tableName] = columns;
        }

        return result;
    }

    /// <summary>拼出单列的 ADD COLUMN 定义：列名 + 类型 + NOT NULL + DEFAULT。</summary>
    private static string BuildAddColumnDefinition(
        Microsoft.EntityFrameworkCore.Metadata.IProperty property,
        string columnName,
        string storeType)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(columnName)).Append(' ').Append(storeType);

        // 补列时列一定是"新"的，库里可能已有存量行。
        // 只要能给出默认值，就必须带上 DEFAULT 并允许为空——否则 NOT NULL 加列会被存量行直接顶失败
        // （PostgreSQL 报 23502: column contains null values）。
        var defaultValueSql = property.GetDefaultValueSql();
        if (!string.IsNullOrWhiteSpace(defaultValueSql))
        {
            builder.Append(" DEFAULT ").Append(defaultValueSql);
            return builder.ToString();
        }

        // 没有 SQL 默认值时，尝试用 CLR 侧配置的默认值（如 bool 的 false）
        var configuredDefault = property.GetDefaultValue();
        if (configuredDefault is not null && !property.IsNullable)
        {
            builder.Append(" DEFAULT ").Append(FormatDefaultValue(configuredDefault, storeType));
            return builder.ToString();
        }

        if (!property.IsNullable)
            builder.Append(" NOT NULL");

        return builder.ToString();
    }

    /// <summary>
    /// 把 CLR 默认值转成 SQL 字面量。
    /// 只覆盖本项目实际用到的类型；遇到不认识的就直接跳过（不加 DEFAULT），
    /// 交由正式迁移处理，避免在这里写出错误的字面量。
    /// </summary>
    private static string FormatDefaultValue(object value, string storeType)
    {
        var text = value switch
        {
            bool boolValue => boolValue ? "TRUE" : "FALSE",
            string stringValue => $"'{stringValue.Replace("'", "''")}'",
            char charValue => $"'{charValue}'",
            Guid guidValue => $"'{guidValue}'",
            int or long or short or byte => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            double or float or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            Enum enumValue => $"'{enumValue}'",
            _ => null,
        };

        return text ?? "NULL";
    }

    /// <summary>
    /// 判断是不是本项目自己映射的业务表。
    /// Identity 的表（AspNetUsers 等）由框架负责演进，交给它自己处理更稳妥。
    /// </summary>
    private static bool IsBusinessTable(string tableName)
        => !tableName.StartsWith("AspNet", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取数据库中每张表实际存在的列（小写化，便于大小写不敏感比较）。</summary>
    private static async Task<Dictionary<string, HashSet<string>>> GetExistingColumnsAsync(
        AiCopilotDbContext dbContext, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name, column_name FROM information_schema.columns";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;

                var table = reader.GetString(0);
                var column = reader.GetString(1);

                if (!columns.TryGetValue(table, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    columns[table] = set;
                }

                set.Add(column);
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }

        return columns;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

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
