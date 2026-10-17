using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EntityFrameworkCore.Configurations;

/// <summary>
/// 会话消息表映射。主键由雪花算法生成，素材与参数快照以 JSON 文本存储。
///
/// 历史回放固定按 <c>Seq</c> 升序读，而 Seq 在会话内唯一，
/// 因此用 (ConversationId, Seq) 建唯一索引：既保证顺序查询走索引，
/// 也能在并发提交时暴露出重复序号的问题，而不是悄悄写进两条乱序消息。
/// </summary>
public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.ToTable("conversation_messages");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ConversationId).IsRequired();
        builder.Property(x => x.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Role).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Text).IsRequired();
        builder.Property(x => x.AssetsJson).IsRequired();
        builder.Property(x => x.ParamsJson).IsRequired();
        builder.Property(x => x.PairMessageId).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        builder.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
        // 双击任务定位高亮时按 taskId 反查消息
        builder.HasIndex(x => new { x.OwnerId, x.TaskId });
    }
}
