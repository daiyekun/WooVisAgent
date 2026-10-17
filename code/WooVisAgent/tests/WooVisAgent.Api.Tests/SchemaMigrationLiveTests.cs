using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WooVisAgent.EntityFrameworkCore;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 针对<b>真实 PostgreSQL</b>的补列验证。
///
/// 背景：本项目不用 EF Migration，靠启动时自动补列。
/// <see cref="SchemaEvolutionTests" /> 只验证 EF 元数据层面的默认值，
/// <b>但没验证补列语句在真实库、面对存量行时能不能跑通</b>——
/// 而 23502 恰恰只在那个时刻才暴露。
///
/// 这条测试连真实库，走与生产代码相同的 DDL 形态：
/// <list type="bullet">
///   <item>先手工执行一次「无 DEFAULT 的 NOT NULL」，确认会 23502（证明场景被覆盖）；</item>
///   <item>再用带 DEFAULT 的语句补列，必须成功；</item>
///   <item>存量行必须被正确回填，不能是 NULL；</item>
///   <item>跑完删掉新增列，让数据库回到测试前状态（可重复执行）。</item>
/// </list>
///
/// 未提供连接串时跳过（本地不一定有库），不拖累 CI。
/// </summary>
public class SchemaMigrationLiveTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("SCHEMA_MIGRATION_TEST_CONNECTION");

    private static readonly string[] VideoColumns =
    [
        "DeliveryJson",
        "SupportsTextToVideo",
        "SupportsImageToVideo",
    ];

    [Fact]
    public async Task 真实库补列必须成功且存量行被回填()
    {
        var connectionString = ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dbContext = CreateContext(connectionString);

        if (!dbContext.Database.IsRelational())
            return;

        if (!await TableExistsAsync(dbContext))
            return;

        await DropVideoColumnsAsync(dbContext);

        try
        {
            // 1) 先手工执行一次「无 DEFAULT 的 NOT NULL」，复现 23502。
            //    这一步证明本测试确实覆盖到了那个失败场景——
            //    如果哪天它居然成功了，说明前提变了，必须重新评估。
            var failedAsExpected = false;

            try
            {
                await ExecuteNonQueryAsync(
                    dbContext,
                    """ALTER TABLE model_configs ADD COLUMN "DeliveryJson" text NOT NULL;""");
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NotNullViolation)
            {
                failedAsExpected = true;
            }

            Assert.True(
                failedAsExpected,
                "无默认值的必填加列应因存量行存在 NULL 而失败（23502）；" +
                "若它成功了，说明库里没有存量行，本测试已失去意义");

            // 2) 带 DEFAULT 的补列语句必须成功（这就是修复后的行为）
            await ExecuteNonQueryAsync(
                dbContext,
                """ALTER TABLE model_configs ADD COLUMN "DeliveryJson" text NOT NULL DEFAULT '{}';""");

            await ExecuteNonQueryAsync(
                dbContext,
                """ALTER TABLE model_configs ADD COLUMN "SupportsTextToVideo" boolean NOT NULL DEFAULT TRUE;""");

            await ExecuteNonQueryAsync(
                dbContext,
                """ALTER TABLE model_configs ADD COLUMN "SupportsImageToVideo" boolean NOT NULL DEFAULT TRUE;""");

            foreach (var column in VideoColumns)
            {
                Assert.True(
                    await ColumnExistsAsync(dbContext, column),
                    $"补列后列 {column} 应当存在");
            }

            // 3) 关键断言：存量行必须被回填成默认值，而不是 NULL
            var nullCount = await ScalarAsync(
                dbContext,
                """
                SELECT COUNT(*)::int
                FROM model_configs
                WHERE "DeliveryJson" IS NULL
                   OR "SupportsTextToVideo" IS NULL
                   OR "SupportsImageToVideo" IS NULL
                """);

            Assert.Equal(0, nullCount);

            // 4) 默认值内容必须与后端语义一致：空 JSON 表示"完全按预设推断"
            var unexpectedValue = await ScalarAsync(
                dbContext,
                """
                SELECT COUNT(*)::int
                FROM model_configs
                WHERE "DeliveryJson" <> '{}'
                """);

            Assert.Equal(0, unexpectedValue);
        }
        finally
        {
            await DropVideoColumnsAsync(dbContext);
        }
    }

    private static AiCopilotDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AiCopilotDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AiCopilotDbContext(options);
    }

    /// <summary>
    /// 执行非格式化 SQL。
    ///
    /// <b>为什么必须转义花括号</b>：<c>ExecuteSqlRawAsync</c> 内部走 <c>string.Format</c>，
    /// 而我们给 <c>DeliveryJson</c> 的默认值正是 <c>'{}'</c>——
    /// 不转义就会被当成格式占位符，直接抛
    /// <c>FormatException: Expected an ASCII digit</c>。
    /// 生产代码里的 <c>ExecuteNonFormattedSqlAsync</c> 正是为此存在。
    /// </summary>
    private static Task ExecuteNonQueryAsync(AiCopilotDbContext dbContext, string sql)
    {
        var escaped = sql
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);

        return dbContext.Database.ExecuteSqlRawAsync(escaped);
    }

    /// <summary>
    /// 走 ADO.NET 原生连接执行只读标量查询。
    ///
    /// 这里刻意<b>不用</b> <c>SqlQueryRaw</c>：它同样会走 <c>string.Format</c>，
    /// 只要 SQL 里出现 <c>{}</c> 就会炸——而本测试必须查询 <c>&lt;&gt; '{}'</c>。
    /// 走原生连接就没有这层格式化，最干净。
    /// </summary>
    private static async Task<int> ScalarAsync(AiCopilotDbContext dbContext, string sql)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        await dbContext.Database.OpenConnectionAsync();

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task<bool> TableExistsAsync(AiCopilotDbContext dbContext)
    {
        var count = await ScalarAsync(
            dbContext,
            """
            SELECT COUNT(*)::int
            FROM information_schema.tables
            WHERE table_name = 'model_configs'
            """);

        return count > 0;
    }

    private static async Task DropVideoColumnsAsync(AiCopilotDbContext dbContext)
    {
        var list = string.Join(", ", VideoColumns.Select(x => $"DROP COLUMN IF EXISTS \"{x}\""));

        await ExecuteNonQueryAsync(dbContext, $"ALTER TABLE model_configs {list};");
    }

    private static async Task<bool> ColumnExistsAsync(AiCopilotDbContext dbContext, string column)
    {
        // 列名来自本文件内的固定白名单，不存在注入风险
        var count = await ScalarAsync(
            dbContext,
            $"""
            SELECT COUNT(*)::int
            FROM information_schema.columns
            WHERE table_name = 'model_configs' AND column_name = '{column}'
            """);

        return count > 0;
    }
}