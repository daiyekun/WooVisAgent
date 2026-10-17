using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EntityFrameworkCore.Configurations;

/// <summary>
/// 模型配置表映射。主键由雪花算法生成（<c>ValueGeneratedNever</c>），
/// 分类以可读字符串落库，便于直接用 SQL 排查。
/// </summary>
public class ModelConfigConfiguration : IEntityTypeConfiguration<ModelConfig>
{
    public void Configure(EntityTypeBuilder<ModelConfig> builder)
    {
        builder.ToTable("model_configs");

        builder.HasKey(x => x.Id);
        // 主键由 ISnowflakeIdGenerator 生成，数据库不做自增
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Category).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.ModelKey).HasMaxLength(128);
        builder.Property(x => x.Endpoint).HasMaxLength(512);
        builder.Property(x => x.ApiKey).HasMaxLength(1024);
        builder.Property(x => x.ParamsJson).IsRequired();

        // 下面三个字段是"给已存在的表补列"用的，必须显式声明数据库默认值。
        //
        // 原因：补列走的是 EnsureMissingColumnsAsync，它靠 EF 元数据里的默认值来拼
        // `ADD COLUMN ... DEFAULT ...`。而 C# 属性初始值（= "{}" / = true）
        // <b>不会</b>进 EF 元数据——没有 HasDefaultValue 的话，补列会发出
        // `ADD COLUMN "DeliveryJson" text NOT NULL`（无 DEFAULT），
        // 存量行立刻被 PostgreSQL 顶掉：23502 column contains null values，
        // 表现为"加了视频字段之后服务起不来了"。
        //
        // HasDefaultValue 同时也是新建表时的 DDL 默认值，两条路径保持一致。
        builder.Property(x => x.DeliveryJson).IsRequired().HasDefaultValue("{}");
        builder.Property(x => x.SupportsTextToVideo).HasDefaultValue(true);
        builder.Property(x => x.SupportsImageToVideo).HasDefaultValue(true);

        builder.Property(x => x.Remark).HasMaxLength(512);

        // 列表查询固定按「当前用户 + 可选分类」过滤
        builder.HasIndex(x => new { x.OwnerId, x.Category });
    }
}
