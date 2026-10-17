using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.VideoService.Dtos;
using WooVisAgentServices.VideoService.Serialization;
using WooVisAgentServices.VideoService.Support;
using WooVisAgentServices.VideoService.Validation;

namespace WooVisAgentServices.VideoService.Commands;

/// <summary>
/// 提交视频生成任务。
///
/// 与图片侧同构：<b>先落库、再发消息</b>。这样 Worker 收到消息时任务一定已存在，
/// 不会出现「消息到了但查不到任务」的空转。
/// </summary>
public record SubmitVideoGenerationCommand(VideoGenerationInput Input)
    : ICommand<Result<VideoGenerationResultDto>>;

public class SubmitVideoGenerationCommandHandler(
    IRepository<GenerationTask> taskRepository,
    IRepository<ModelConfig> modelRepository,
    IRepository<Conversation> conversationRepository,
    IRepository<ConversationMessage> messageRepository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator,
    IGenerationTaskDispatcher dispatcher,
    ILogger<SubmitVideoGenerationCommandHandler> logger)
    : ICommandHandler<SubmitVideoGenerationCommand, Result<VideoGenerationResultDto>>
{
    public async Task<Result<VideoGenerationResultDto>> Handle(
        SubmitVideoGenerationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var input = command.Input;
        var error = VideoGenerationInputValidator.Validate(input);
        if (error is not null)
            return Result.Invalid(error);

        var ownerId = currentUser.Id;
        var modelConfigId = long.Parse(input.ModelId.Trim(), CultureInfo.InvariantCulture);

        var model = await modelRepository.GetAsync(
            x => x.Id == modelConfigId && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (model is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        if (model.Category != ModelCategory.Video)
            return Result.Invalid("该模型不是视频模型，无法用于视频生成。");

        if (!model.Enabled)
            return Result.Invalid("该模型已停用，请先在「模型配置」中启用。");

        var now = DateTimeOffset.UtcNow;
        var firstFrameUrl = VideoGenerationInputValidator.NormalizeUrl(input.FirstFrameUrl);

        // 能力校验放在这里而不是 Worker：模型声明的「只支持文生 / 只支持图生」
        // 必须在提交前就拦下来。让请求进了队列才失败，用户已经看到"已提交"了。
        var capabilityError = VideoGenerationInputValidator.ValidateCapabilities(input, model, firstFrameUrl is not null);
        if (capabilityError is not null)
            return Result.Invalid(capabilityError);

        // 首帧图作为任务输入素材落库，Worker 原样读取
        var inputs = firstFrameUrl is null
            ? new List<TaskAsset>()
            :
            [
                new TaskAsset
                {
                    Id = idGenerator.NextId().ToString(CultureInfo.InvariantCulture),
                    Type = "image",
                    Url = firstFrameUrl,
                    FileName = Path.GetFileName(new Uri(firstFrameUrl, UriKind.RelativeOrAbsolute).AbsolutePath),
                }
            ];

        // 解析所属会话。两个分支都会赋值，else 分支保证「不传也能拿到一个会话」
        Conversation? conversation = null;
        var isNewConversation = false;

        if (!string.IsNullOrWhiteSpace(input.ConversationId))
        {
            if (!long.TryParse(input.ConversationId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var conversationId))
                return Result.Invalid("会话标识不合法。");

            conversation = await conversationRepository.GetAsync(
                x => x.Id == conversationId && x.OwnerId == ownerId,
                cancellationToken: cancellationToken);

            if (conversation is null)
                return Result.NotFound("会话不存在或无权访问。");
        }
        else
        {
            // 没传会话就自动建一个（复用最近一条空会话，避免连点两次堆出两条空记录）。
            // 这里不能 AsNoTracking：复用的会话后面还要更新 MessageCount / TaskCount。
            conversation = await conversationRepository.GetQueryable()
                .Where(x => x.OwnerId == ownerId && x.MessageCount == 0)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (conversation is null)
            {
                conversation = new Conversation
                {
                    Id = idGenerator.NextId(),
                    Title = "新建创作",
                    OwnerId = ownerId,
                    Type = GenerationTaskType.Video,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                conversationRepository.Add(conversation);
                isNewConversation = true;
            }
        }

        var targetConversation = conversation!;
        var paramsJson = VideoGenerationSerialization.SerializeParams(
            VideoGenerationInputValidator.BuildParamsSnapshot(input));

        var task = new GenerationTask
        {
            Id = idGenerator.NextId(),
            Name = string.IsNullOrWhiteSpace(input.Name)
                ? VideoTaskNaming.FromPrompt(input.Prompt)
                : input.Name!.Trim(),
            OwnerId = ownerId,
            Type = GenerationTaskType.Video,
            Status = GenerationTaskStatus.Queued,
            Progress = 0,
            ModelConfigId = model.Id,
            ModelName = model.Name,
            Provider = model.Provider,
            Prompt = input.Prompt.Trim(),
            NegativePrompt = VideoGenerationInputValidator.NormalizeOptional(
                input.NegativePrompt, VideoGenerationInputValidator.MaxNegativePromptLength),
            ParamsJson = paramsJson,
            InputsJson = VideoGenerationSerialization.SerializeAssets(inputs),
            OutputsJson = "[]",
            ConversationId = targetConversation.Id,
            CreatedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        };

        taskRepository.Add(task);

        // 会话内的一次生成 = 一对消息（用户需求 + 助手结果），
        // 与图片侧保持一致，前端的会话回放才能通用
        var nextSeq = await messageRepository.GetQueryable()
            .Where(x => x.ConversationId == targetConversation.Id)
            .Select(x => (int?)x.Seq)
            .MaxAsync(cancellationToken) ?? 0;

        var userMessageId = idGenerator.NextId();
        var assistantMessageId = idGenerator.NextId();

        messageRepository.Add(new ConversationMessage
        {
            Id = userMessageId,
            ConversationId = targetConversation.Id,
            OwnerId = ownerId,
            Role = "user",
            Text = input.Prompt.Trim(),
            PairMessageId = assistantMessageId.ToString(CultureInfo.InvariantCulture),
            AssetsJson = VideoGenerationSerialization.SerializeAssets(inputs),
            ParamsJson = paramsJson,
            Seq = nextSeq + 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        messageRepository.Add(new ConversationMessage
        {
            Id = assistantMessageId,
            ConversationId = targetConversation.Id,
            OwnerId = ownerId,
            Role = "assistant",
            Text = firstFrameUrl is not null ? "收到首帧图，正在生成视频…" : "正在生成视频…",
            TaskId = task.Id,
            PairMessageId = userMessageId.ToString(CultureInfo.InvariantCulture),
            AssetsJson = "[]",
            ParamsJson = paramsJson,
            Seq = nextSeq + 2,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // 聊天页会预先创建一个默认 Image 类型的空会话；首个任务若是视频，
        // 必须在落库前纠正类型，否则“视频会话”筛选永远找不到它。
        if (targetConversation.TaskCount == 0)
            targetConversation.Type = GenerationTaskType.Video;

        targetConversation.MessageCount += 2;
        targetConversation.TaskCount += 1;
        targetConversation.UpdatedAt = now;

        if (targetConversation.MessageCount == 2 && targetConversation.Title == "新建创作")
            targetConversation.Title = VideoTaskNaming.FromPrompt(input.Prompt);

        // 只在「复用已有会话」时才显式 Update：新会话已经 Add 过，
        // 再 Update 会被强转成 Modified 而生成 UPDATE，抛并发异常
        if (!isNewConversation)
            conversationRepository.Update(targetConversation);

        await taskRepository.SaveChangesAsync(cancellationToken);

        try
        {
            // 复用图片侧的通用派发器：它已按任务类型分派，
            // 因此视频重试走的也是同一条路径，不会漏掉视频分支
            await dispatcher.DispatchAsync(task, cancellationToken);
        }
        catch (Exception exception)
        {
            // 消息发不出去（如 RabbitMQ 不可用）时任务会永远停在排队中，
            // 这里直接置为失败并写明原因，避免出现"假排队"
            logger.LogError(exception, "视频任务 {TaskId} 投递到消息队列失败", task.Id);

            task.Status = GenerationTaskStatus.Failed;
            task.ErrorMessage = $"任务投递到消息队列失败：{exception.Message}";
            task.FinishedAt = DateTimeOffset.UtcNow;
            task.UpdatedAt = DateTimeOffset.UtcNow;

            taskRepository.Update(task);
            await taskRepository.SaveChangesAsync(cancellationToken);

            return Result.Failure("任务已创建，但投递到消息队列失败，请稍后重试。");
        }

        return Result.Success(new VideoGenerationResultDto(
            task.Id.ToString(CultureInfo.InvariantCulture),
            task.Status.ToWireValue()));
    }
}