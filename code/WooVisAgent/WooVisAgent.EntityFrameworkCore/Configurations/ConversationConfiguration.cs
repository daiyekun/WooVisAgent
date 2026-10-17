using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EntityFrameworkCore.Configurations;

/// <summary>
/// 会话表映射。主键由雪花算法生成（<c>ValueGeneratedNever</c>），与其它业务表一致。
///
/// 与 <c>material_assets</c> 同理，这里对消息表只做逻辑关联、不建外键：
/// 删除会话时由用例显式级联删除消息与任务，写明顺序比交给数据库级联更可控
/// （尤其是"任务要删、已选秀的作品要留"这条规则必须在代码里显式表达）。
/// </summary>
public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Title).IsRequired().HasMaxLength(256);
        builder.Property(x => x.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).IsRequired();

        // 侧边栏「最近会话」与列表页都按「当前用户 + 最后修改时间倒序」取，
        // 这是最高频的查询，必须走索引而不是全表排序。
        builder.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        // 列表页支持按图片 / 视频分类筛选
        builder.HasIndex(x => new { x.OwnerId, x.Type });
    }
}
