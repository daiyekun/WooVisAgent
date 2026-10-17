using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EntityFrameworkCore.Configurations;

/// <summary>
/// 生成任务表映射。主键由雪花算法生成（<c>ValueGeneratedNever</c>），
/// 类型与状态以可读字符串落库，便于直接用 SQL 排查。
/// 素材（输入 / 产物）与参数快照以 JSON 文本存储，读出后在用例层反序列化。
/// </summary>
public class GenerationTaskConfiguration : IEntityTypeConfiguration<GenerationTask>
{
    public void Configure(EntityTypeBuilder<GenerationTask> builder)
    {
        builder.ToTable("generation_tasks");

        builder.HasKey(x => x.Id);
        // 主键由 ISnowflakeIdGenerator 生成，数据库不做自增
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(256);
        builder.Property(x => x.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.ModelName).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Prompt).IsRequired();
        builder.Property(x => x.NegativePrompt).HasMaxLength(2000);
        builder.Property(x => x.ParamsJson).IsRequired();
        builder.Property(x => x.InputsJson).IsRequired();
        builder.Property(x => x.OutputsJson).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.Property(x => x.IsSelected).HasDefaultValue(false);

        // 列表查询固定按「当前用户 + 可选类型/状态」过滤，并按创建时间倒序
        builder.HasIndex(x => new { x.OwnerId, x.Status });
        builder.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        // 聊天页加载历史时按会话取全部任务；删除会话时也要按会话批量删
        builder.HasIndex(x => new { x.OwnerId, x.ConversationId });
        // Worker 侧按主键回查，无需额外索引（主键即索引）
    }
}
