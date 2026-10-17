using Microsoft.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>
/// 删除会话，并级联删除它下面的一切：
/// <list type="bullet">
///   <item>会话本身；</item>
///   <item>该会话的全部消息；</item>
///   <item>该会话下的全部生成任务。</item>
/// </list>
///
/// <para>
/// <b>已经「选秀」进我的作品库的产物一律保留。</b>
/// 这是本操作唯一需要小心的规则：
/// 作品表 <c>material_assets</c> 只用逻辑字段 <c>SourceTaskId</c> 关联任务、不建外键，
/// 所以任务被物理删除后作品依然在，<see cref="MaterialAsset.SourceTaskName" />
/// 这类快照字段也已经冗余存好，不会因为任务消失而查不到标题。
/// </para>
///
/// <para>
/// 删除时还要把仍在跑的任务先落成终态：否则 RabbitMQ 里已投递的消息
/// 会被 Worker 消费后回查不到任务，进而在死信队列里反复重试。
/// </para>
/// </summary>
public record DeleteConversationCommand(long Id) : ICommand<Result<DeleteConversationResultDto>>;

public class DeleteConversationCommandHandler(
    IRepository<Conversation> conversationRepository,
    IRepository<ConversationMessage> messageRepository,
    IRepository<GenerationTask> taskRepository,
    ICurrentUser currentUser)
    : ICommandHandler<DeleteConversationCommand, Result<DeleteConversationResultDto>>
{
    public async Task<Result<DeleteConversationResultDto>> Handle(
        DeleteConversationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;

        var conversation = await conversationRepository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (conversation is null)
            return Result.NotFound("会话不存在或无权访问。");

        // 1. 先统计影响面，用于回给前端做提示（"已删除 N 个任务，保留 M 件作品"）
        var taskCount = await taskRepository.GetCountAsync(
            x => x.OwnerId == ownerId && x.ConversationId == command.Id,
            cancellationToken);

        var messageCount = await messageRepository.GetCountAsync(
            x => x.OwnerId == ownerId && x.ConversationId == command.Id,
            cancellationToken);

        // 2. 把还在跑的任务置为已取消。
        //    注意这里只改状态，实体先留着，等下统一删。
        //    用 IsFinal() 判定而不是字符串比较：枚举在库里存的是字符串，
        //    直接比 ToString() 既啰嗦又容易漏掉新增的状态。
        var runningTasks = await taskRepository.GetQueryable()
            .Where(x => x.OwnerId == ownerId
                        && x.ConversationId == command.Id
                        && (int)x.Status < (int)GenerationTaskStatus.Succeeded)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var task in runningTasks)
        {
            task.Status = GenerationTaskStatus.Canceled;
            task.ErrorMessage = "所属会话已被删除，任务自动取消。";
            task.FinishedAt = now;
            task.UpdatedAt = now;
            taskRepository.Update(task);
        }

        // 3. 统计「已选秀」的任务数 —— 这些任务本身会被删，
        //    但它们的作品已独立入库，会留在我的作品库里。
        //    把这个数字回给前端，让删除后的提示能说清"作品还在"。
        var keptCount = await taskRepository.GetQueryable()
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId
                        && x.ConversationId == command.Id
                        && x.IsSelected)
            .CountAsync(cancellationToken);

        // 4. 删除会话下的任务
        //    Delete() 只是打删除标记，同一 DbContext 里实体仍处于跟踪状态，
        //    所以上面的计数必须在打标记之前完成。
        var tasksToDelete = await taskRepository.GetQueryable()
            .Where(x => x.OwnerId == ownerId && x.ConversationId == command.Id)
            .ToListAsync(cancellationToken);

        foreach (var task in tasksToDelete)
        {
            taskRepository.Delete(task);
        }

        // 5. 删除会话下的消息
        var messagesToDelete = await messageRepository.GetQueryable()
            .Where(x => x.OwnerId == ownerId && x.ConversationId == command.Id)
            .ToListAsync(cancellationToken);

        foreach (var message in messagesToDelete)
        {
            messageRepository.Delete(message);
        }

        // 6. 最后删会话本身
        conversationRepository.Delete(conversation);

        await conversationRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new DeleteConversationResultDto(
            messagesToDelete.Count,
            tasksToDelete.Count,
            keptCount));
    }
}
