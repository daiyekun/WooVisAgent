using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WooVisAgent.SharedLib.Domain;


namespace WooVisAgent.EntityFrameworkCore;

public class AiCopilotDbContext(DbContextOptions<AiCopilotDbContext> options) : IdentityDbContext(options)
{
    /// <summary>用户自行接入的模型配置（映射见 Configurations/ModelConfigConfiguration.cs）</summary>
    public DbSet<ModelConfig> ModelConfigs => Set<ModelConfig>();

    /// <summary>图片 / 视频生成任务（映射见 Configurations/GenerationTaskConfiguration.cs）</summary>
    public DbSet<GenerationTask> GenerationTasks => Set<GenerationTask>();

    /// <summary>用户的作品 / 产品（映射见 Configurations/MaterialAssetConfiguration.cs）</summary>
    public DbSet<MaterialAsset> MaterialAssets => Set<MaterialAsset>();

    // AiGateway 实体模型
    //public DbSet<LanguageModel> LanguageModels => Set<LanguageModel>();
    //public DbSet<ConversationTemplate> ConversationTemplates => Set<ConversationTemplate>();
    //public DbSet<Session> Sessions => Set<Session>();
    //public DbSet<Message> Messages => Set<Message>();
    
    // RAG 实体模型
    //public DbSet<EmbeddingModel> EmbeddingModels => Set<EmbeddingModel>();
    //public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();
    //public DbSet<Document> Documents => Set<Document>();
    //public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    
    //public DbSet<BusinessDatabase> BusinessDatabases => Set<BusinessDatabase>();
    
    //public DbSet<McpServerInfo> McpServerInfos => Set<McpServerInfo>();

    
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}