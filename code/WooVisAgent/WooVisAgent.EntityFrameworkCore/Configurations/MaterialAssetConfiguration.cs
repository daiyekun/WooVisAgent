using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EntityFrameworkCore.Configurations;

/// <summary>
/// 作品表映射。主键由雪花算法生成（<c>ValueGeneratedNever</c>），
/// 与 <c>generation_tasks</c> 一样不依赖数据库自增。
///
/// 任务删除后作品仍需保留，因此这里用逻辑关联（只存 SourceTaskId，不建外键），
/// 避免任务被删时连带删掉用户已经"选秀"入库的成果。
/// </summary>
public class MaterialAssetConfiguration : IEntityTypeConfiguration<MaterialAsset>
{
    public void Configure(EntityTypeBuilder<MaterialAsset> builder)
    {
        builder.ToTable("material_assets");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Type).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Url).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(256);
        builder.Property(x => x.SourceAssetId).HasMaxLength(64);
        builder.Property(x => x.SourceTaskName).HasMaxLength(256);
        builder.Property(x => x.GroupName).HasMaxLength(64);
        builder.Property(x => x.Prompt);

        // 作品库固定按「当前用户 + 创建时间倒序」分页
        builder.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        // 按任务反查作品，用于「该任务是否已入选」以及避免同一产物重复入库
        builder.HasIndex(x => new { x.OwnerId, x.SourceTaskId });
    }
}