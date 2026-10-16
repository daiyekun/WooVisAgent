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
        builder.Property(x => x.Remark).HasMaxLength(512);

        // 列表查询固定按「当前用户 + 可选分类」过滤
        builder.HasIndex(x => new { x.OwnerId, x.Category });
    }
}
