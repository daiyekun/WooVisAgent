using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Queries;

/// <summary>
/// 分页查询当前用户的创作会话，可按类型（图片 / 视频）筛选。
///
/// 排序固定按 <c>UpdatedAt</c> 倒序 —— 侧边栏「最近会话」和「更多会话」
/// 用的是同一个口径：用户心智里的"最近"是最近聊过，不是最近创建。
/// 对应前端 <c>ConversationListView.vue</c> 与侧边栏的最近会话区。
/// </summary>
public record GetConversationsQuery(
    string? Type = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 12) : IQuery<Result<PagedResultDto<ConversationDto>>>;

public class GetConversationsQueryHandler(
    IRepository<Conversation> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetConversationsQuery, Result<PagedResultDto<ConversationDto>>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResultDto<ConversationDto>>> Handle(
        GetConversationsQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 12 : Math.Min(query.PageSize, MaxPageSize);

        var sql = repository.GetQueryable()
            .Where(x => x.OwnerId == currentUser.Id);

        // 类型筛选沿用作品库的口径：非法值忽略而不是返回空列表
        if (GenerationTaskTypeExtensions.TryParseWireValue(query.Type, out var type))
        {
            sql = sql.Where(x => x.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = $"%{query.Keyword.Trim()}%";
            sql = sql.Where(x => EF.Functions.Like(x.Title, keyword));
        }

        var total = await sql.CountAsync(cancellationToken);

        var items = await sql
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResultDto<ConversationDto>(
            items.Select(x => x.ToDto()).ToList(),
            total,
            page,
            pageSize));
    }
}

/// <summary>
/// 会话总数 + 图片 / 视频分类计数，供侧边栏「更多会话(N)」与分类页签使用。
/// </summary>
public record GetConversationSummaryQuery : IQuery<Result<ConversationSummaryDto>>;

public sealed record ConversationSummaryDto(
    int Total,
    int ImageCount,
    int VideoCount);

public class GetConversationSummaryQueryHandler(
    IRepository<Conversation> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetConversationSummaryQuery, Result<ConversationSummaryDto>>
{
    public async Task<Result<ConversationSummaryDto>> Handle(
        GetConversationSummaryQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;

        // 一次查询把三个计数拿回来，避免为了侧边栏角标连发三次请求
        var row = await repository.GetQueryable()
            .Where(x => x.OwnerId == ownerId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Image = g.Count(x => x.Type == GenerationTaskType.Image),
                Video = g.Count(x => x.Type == GenerationTaskType.Video),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return Result.Success(new ConversationSummaryDto(0, 0, 0));
        }

        return Result.Success(new ConversationSummaryDto(row.Total, row.Image, row.Video));
    }
}

/// <summary>
/// 会话详情：会话本身 + 全部消息 + 该会话下的任务。
///
/// 聊天页靠它完成"打开一个会话就还原整段对话历史"，
/// 双击任务定位时也会带上 highlightTaskId 让前端滚到对应消息。
/// </summary>
public record GetConversationDetailQuery(
    long Id,
    long? HighlightTaskId = null) : IQuery<Result<ConversationDetailDto>>;

public class GetConversationDetailQueryHandler(
    IRepository<Conversation> repository,
    IRepository<ConversationMessage> messageRepository,
    IRepository<GenerationTask> taskRepository,
    ICurrentUser currentUser)
    : IQueryHandler<GetConversationDetailQuery, Result<ConversationDetailDto>>
{
    public async Task<Result<ConversationDetailDto>> Handle(
        GetConversationDetailQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;

        var conversation = await repository.GetAsync(
            x => x.Id == query.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (conversation is null)
            return Result.NotFound("会话不存在或无权访问。");

        var messages = await messageRepository.GetQueryable()
            .AsNoTracking()
            .Where(x => x.ConversationId == query.Id && x.OwnerId == ownerId)
            .OrderBy(x => x.Seq)
            .ToListAsync(cancellationToken);

        var tasks = await taskRepository.GetQueryable()
            .AsNoTracking()
            .Where(x => x.ConversationId == query.Id && x.OwnerId == ownerId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        // 打开会话本身就是一次"访问"，把 UpdatedAt 推到最新，
        // 这样它在最近会话里的排序会跟着用户真实行为走，而不是停留在创建时刻。
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        repository.Update(conversation);
        await repository.SaveChangesAsync(cancellationToken);

        var detail = new ConversationDetailDto(
            conversation.ToDto(),
            messages.Select(x => x.ToDto()).ToList(),
            tasks.Select(x => x.ToDto()).ToList());

        return Result.Success(detail);
    }
}

/// <summary>新建一个空会话。聊天页首次发消息前会先拿到 id，后续任务都挂到它下面。</summary>
public record CreateConversationCommand(
    string? Title = null) : ICommand<Result<ConversationDto>>;

public class CreateConversationCommandHandler(
    IRepository<Conversation> repository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator)
    : ICommandHandler<CreateConversationCommand, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(
        CreateConversationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var now = DateTimeOffset.UtcNow;

        var conversation = new Conversation
        {
            Id = idGenerator.NextId(),
            // 标题留空时用"新建创作"占位，首条消息进来后会被真实提示词替换
            Title = string.IsNullOrWhiteSpace(command.Title)
                ? "新建创作"
                : command.Title.Trim(),
            OwnerId = currentUser.Id,
            Type = GenerationTaskType.Image,
            CreatedAt = now,
            UpdatedAt = now,
        };

        repository.Add(conversation);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(conversation.ToDto());
    }
}

/// <summary>改会话标题。聊天页在首条消息进来后自动调用一次，把占位标题换成提示词摘要。</summary>
public record RenameConversationCommand(
    long Id,
    string Title) : ICommand<Result<ConversationDto>>;

public class RenameConversationCommandHandler(
    IRepository<Conversation> repository,
    ICurrentUser currentUser)
    : ICommandHandler<RenameConversationCommand, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(
        RenameConversationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var title = command.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return Result.Invalid("会话标题不能为空。");

        if (title.Length > 120)
            title = title[..120];

        var conversation = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == currentUser.Id,
            cancellationToken: cancellationToken);

        if (conversation is null)
            return Result.NotFound("会话不存在或无权访问。");

        conversation.Title = title;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        repository.Update(conversation);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(conversation.ToDto());
    }
}

/// <summary>
/// 取（或创建）当前用户的活跃会话。
///
/// 聊天页进入时还不知道会话 id，如果在提交时才建会话，
/// 用户在空白页反复输了字又删掉就会留下一堆空会话。
/// 因此进入聊天页就先调这个接口拿一个占位会话，
/// 首条消息提交成功后再由前端决定要不要留着它。
/// </summary>
public record EnsureConversationCommand : ICommand<Result<ConversationDto>>;

public class EnsureConversationCommandHandler(
    IRepository<Conversation> repository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator)
    : ICommandHandler<EnsureConversationCommand, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(
        EnsureConversationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var now = DateTimeOffset.UtcNow;

        // 优先复用最近一个空会话：这样"新建创作"按钮连点两次也不会堆出两条空记录
        var reusable = await repository.GetQueryable()
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.MessageCount == 0)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (reusable is not null)
            return Result.Success(reusable.ToDto());

        var conversation = new Conversation
        {
            Id = idGenerator.NextId(),
            Title = "新建创作",
            OwnerId = ownerId,
            Type = GenerationTaskType.Image,
            CreatedAt = now,
            UpdatedAt = now,
        };

        repository.Add(conversation);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(conversation.ToDto());
    }
}

/// <summary>会话 id 转字符串的公共出口，避免各处重复写 CultureInfo</summary>
internal static class ConversationIdExtensions
{
    public static string ToWireId(this long id) => id.ToString(CultureInfo.InvariantCulture);
}
