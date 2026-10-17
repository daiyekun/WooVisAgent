using Microsoft.EntityFrameworkCore;
using WooVisAgent.EntityFrameworkCore;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 「给已有表补列」的回归测试。
///
/// <para>
/// 背景：本项目不用 EF Migration，而是启动时自建表 + 自动补列
/// （<c>InitializeDatabaseAsync</c> 里的 EnsureCreated / EnsureMissingTables /
/// EnsureMissingColumns 三段式）。它给"改实体、加字段"省掉了写迁移的麻烦，
/// 但代价是<b>新增字段必须自己保证能被安全补上</b>。
/// </para>
///
/// <para>
/// 踩过的坑：给 <c>ModelConfig</c> 加 <c>DeliveryJson</c>（非空字符串）时，
/// 实体里写了 <c>= "{}"</c> 就以为有了默认值。结果启动直接报
/// <c>23502: column "DeliveryJson" ... contains null values</c>——
/// 因为<b>C# 属性初始值不会进入 EF 元数据</b>，
/// 补列逻辑读不到默认值，于是发出 <c>ADD COLUMN ... NOT NULL</c>（无 DEFAULT），
/// 存量行当场被顶掉，服务起不来。
/// </para>
///
/// <para>
/// 这些测试把规则固化成"断言"：<b>所有非空且没有数据库默认值的列，都必须被显式发现</b>。
/// 这样下一个人加必填字段时，测试会立刻告诉他缺 HasDefaultValue，
/// 而不必等启动失败、且只看到一句 23502 去猜。
/// </para>
/// </summary>
public class SchemaEvolutionTests
{
    /// <summary>由 EF 自动建表管理的业务表（与 EnsureMissingColumns 只处理业务表一致）</summary>
    private static readonly string[] BusinessTables =
    [
        "generation_tasks",
        "model_configs",
        "conversations",
        "conversation_messages",
        "material_assets",
    ];

    /// <summary>
    /// 用 EF 的内存模型跑一遍：找出所有「必填但没有数据库默认值」的列。
    /// </summary>
    private static List<string> FindRequiredColumnsWithoutDefault()
    {
        var options = new DbContextOptionsBuilder<AiCopilotDbContext>()
            .UseInMemoryDatabase("schema-evolution-probe")
            .Options;

        using var dbContext = new AiCopilotDbContext(options);

        var problems = new List<string>();

        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            if (string.IsNullOrWhiteSpace(tableName)
                || !BusinessTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.IsNullable)
                    continue;

                // 有 SQL 默认值或 CLR 默认值都算安全
                if (!string.IsNullOrWhiteSpace(property.GetDefaultValueSql()))
                    continue;

                if (property.GetDefaultValue() is not null)
                    continue;

                problems.Add($"{tableName}.{property.GetColumnName()}");
            }
        }

        return problems;
    }

    /// <summary>
    /// 已经「安全」的必填列白名单。
    ///
    /// 这些列要么在项目启用自动补列之前就已存在（那时是靠 EF 迁移建的），
    /// 要么本身就有数据库默认值。之所以显式列出而不是笼统放行，
    /// 是为了让白名单保持可审查——新增必填列时如果忘了配默认值，
    /// 测试会立刻报错并出现在这个列表的视线里，而不是被静默放过。
    /// </summary>
    private static readonly HashSet<string> KnownRequiredColumnsWithoutDefault = new(StringComparer.OrdinalIgnoreCase)
    {
        "generation_tasks.Name",
        "generation_tasks.OwnerId",
        "generation_tasks.ModelName",
        "generation_tasks.Provider",
        "generation_tasks.Prompt",
        "generation_tasks.ParamsJson",
        "generation_tasks.InputsJson",
        "generation_tasks.OutputsJson",
        "conversations.Title",
        "conversations.OwnerId",
        "conversation_messages.OwnerId",
        "conversation_messages.Role",
        "conversation_messages.Text",
        "conversation_messages.AssetsJson",
        "conversation_messages.ParamsJson",
        "material_assets.OwnerId",
        "material_assets.Type",
        "material_assets.Url",
        "model_configs.Name",
        "model_configs.OwnerId",
        "model_configs.Provider",
        "model_configs.ParamsJson",
    };

    [Fact]
    public void 所有必填列都必须声明数据库默认值()
    {
        var problems = FindRequiredColumnsWithoutDefault();

        // 只允许白名单里的历史列豁免；任何「新出现的」无默认值必填列都算失败
        var unexpected = problems
            .Where(column => !KnownRequiredColumnsWithoutDefault.Contains(column))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "以下必填列没有数据库默认值，给已有表补列时会因存量行为 NULL 而失败" +
            $"（PostgreSQL 23502）。请在 EntityTypeConfiguration 里补 .HasDefaultValue(...)：" +
            $"注意 C# 属性初始值不会进入 EF 元数据。" +
            $"问题列：{string.Join("、", unexpected)}");
    }

    [Fact]
    public void 历史豁免列表不应随意膨胀()
    {
        // 白名单里的列必须真的还缺默认值，否则说明有人补了默认值、
        // 却忘了把它从豁免列表里移走——留着会让白名单逐渐变成"什么都往里塞"
        var stillMissing = FindRequiredColumnsWithoutDefault()
            .Where(column => KnownRequiredColumnsWithoutDefault.Contains(column))
            .ToList();

        var stale = KnownRequiredColumnsWithoutDefault
            .Where(column => !stillMissing.Contains(column, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"以下列已不再缺少默认值，请从历史豁免列表中移除：{string.Join("、", stale)}");
    }

    [Fact]
    public void 视频交付字段应带有正确的数据库默认值()
    {
        var options = new DbContextOptionsBuilder<AiCopilotDbContext>()
            .UseInMemoryDatabase("schema-evolution-video-probe")
            .Options;

        using var dbContext = new AiCopilotDbContext(options);

        var entityType = dbContext.Model.FindEntityType(
            typeof(WooVisAgent.SharedLib.Domain.ModelConfig));

        Assert.NotNull(entityType);

        var delivery = entityType!.FindProperty(nameof(WooVisAgent.SharedLib.Domain.ModelConfig.DeliveryJson));
        var text = entityType.FindProperty(nameof(WooVisAgent.SharedLib.Domain.ModelConfig.SupportsTextToVideo));
        var image = entityType.FindProperty(nameof(WooVisAgent.SharedLib.Domain.ModelConfig.SupportsImageToVideo));

        Assert.NotNull(delivery);
        Assert.NotNull(text);
        Assert.NotNull(image);

        // 交付模板：默认空 JSON，表示"完全按预设推断"
        Assert.Equal("{}", delivery!.GetDefaultValue());
        Assert.False(delivery.IsNullable);

        // 两种能力默认都支持：绝大多数模型两种都支持，
        // 用户按实际情况在模型配置页取消勾选即可
        Assert.Equal(true, text!.GetDefaultValue());
        Assert.Equal(true, image!.GetDefaultValue());
    }

    [Fact]
    public void 探测器本身应有效()
    {
        // 防止「探测器写错了导致永远返回空」这种死测试：
        // 用一个已知必填、无默认值的列名去反查，探测器必须能找得到它
        var problems = FindRequiredColumnsWithoutDefault();

        Assert.Contains(
            problems,
            column => column.EndsWith("generation_tasks.Prompt", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(KnownRequiredColumnsWithoutDefault);
    }
}
