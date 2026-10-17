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
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.ImageService.Support;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>
/// 提交图片生成任务。
///
/// 执行顺序很重要：<b>先落库、再发消息</b>。
/// 这样 Worker 收到消息时任务一定已存在，不会出现"消息到了但查不到任务"的空转；
/// 反过来若先发消息再落库，Worker 可能比事务提交更早地读到任务。
/// </summary>
public record SubmitImageGenerationCommand(ImageGenerationInput Input)
    : ICommand<Result<ImageGenerationResultDto>>;

public class SubmitImageGenerationCommandHandler(
    IRepository<GenerationTask> taskRepository,
    IRepository<ModelConfig> modelRepository,
    IRepository<Conversation> conversationRepository,
    IRepository<ConversationMessage> messageRepository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator,
    IGenerationTaskDispatcher dispatcher,
    ILogger<SubmitImageGenerationCommandHandler> logger)
    : ICommandHandler<SubmitImageGenerationCommand, Result<ImageGenerationResultDto>>
{
    public async Task<Result<ImageGenerationResultDto>> Handle(
        SubmitImageGenerationCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var error = ImageGenerationInputValidator.Validate(command.Input);
        if (error is not null)
            return Result.Invalid(error);

        var input = command.Input;
        var ownerId = currentUser.Id;
        var modelConfigId = long.Parse(input.ModelId.Trim(), CultureInfo.InvariantCulture);

        var model = await modelRepository.GetAsync(
            x => x.Id == modelConfigId && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (model is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        if (model.Category != ModelCategory.Image)
            return Result.Invalid("该模型不是图片模型，无法用于图片生成。");

        if (!model.Enabled)
            return Result.Invalid("该模型已停用，请先在「模型配置」中启用。");

        var now = DateTimeOffset.UtcNow;

        // 参考图作为任务输入素材记录，Worker 原样读取
        var referenceImages = ImageGenerationInputValidator.NormalizeUrls(input.ReferenceImageUrls)
            .Select(url => new TaskAsset
            {
                Id = idGenerator.NextId().ToString(CultureInfo.InvariantCulture),
                Type = "image",
                Url = url,
                FileName = Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).ToString()),
            })
            .ToList();

        // 解析所属会话。两个分支都会赋值，else 分支保证「不传也能拿到一个会话」，
        // 所以走到后面时 conversation 必然非空。
        Conversation? conversation = null;

        // 是否是本次新建的会话。决定后面要不要显式 Update：
        // 新建的已经 Add 过（EF 状态 Added），再 Update 会被强转成 Modified 而报并发异常。
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
            // 没传会话就<b>自动建一个</b>，不能让这次生成变成无根之木。
            //
            // 之前这里是「不传就不建」，于是 task.ConversationId 直接为 null：
            // 任务能在「任务列表」里查到，但它不属于任何会话，
            // 侧边栏「最近会话」永远为空，重新登录后这段对话也找不回来——
            // 表现就是「新建的会话没保存住，历史记录看不见」。
            //
            // 前端从「新建创作」进入时拿不到 conversationId（会话要到提交后才产生），
            // 这条兜底就是为那个场景准备的。复用最近一个空会话，
            // 避免用户连点两次「新建创作」堆出两条空记录。
            //
            // 这里<b>不能</b>用 AsNoTracking：复用的会话后面还要更新
            // MessageCount / TaskCount / UpdatedAt，未被跟踪的实体调 Update()
            // 会报 DbUpdateConcurrencyException（期望影响 1 行，实际 0 行）。
            conversation = await conversationRepository.GetQueryable()
                .Where(x => x.OwnerId == ownerId && x.MessageCount == 0)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (conversation is null)
            {
                // 标题留占位，等下面第一条消息落库时统一换成提示词摘要
                conversation = new Conversation
                {
                    Id = idGenerator.NextId(),
                    Title = "新建创作",
                    OwnerId = ownerId,
                    Type = GenerationTaskType.Image,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                conversationRepository.Add(conversation);
                isNewConversation = true;
            }
        }

        // 两个分支都已确保 conversation 有值，这里收口一下可空性
        var targetConversation = conversation!;

        var task = new GenerationTask
        {
            Id = idGenerator.NextId(),
            Name = string.IsNullOrWhiteSpace(input.Name)
                ? GenerationTaskNaming.FromPrompt(input.Prompt)
                : input.Name!.Trim(),
            OwnerId = ownerId,
            Type = GenerationTaskType.Image,
            Status = GenerationTaskStatus.Queued,
            Progress = 0,
            ModelConfigId = model.Id,
            ModelName = model.Name,
            Provider = model.Provider,
            Prompt = input.Prompt.Trim(),
            NegativePrompt = ImageGenerationInputValidator.NormalizeOptional(
                input.NegativePrompt, ImageGenerationInputValidator.MaxNegativePromptLength),
            ParamsJson = GenerationTaskSerialization.SerializeParams(
                ImageGenerationInputValidator.BuildParamsSnapshot(input)),
            InputsJson = GenerationTaskSerialization.SerializeAssets(referenceImages),
            OutputsJson = "[]",
            ConversationId = targetConversation.Id,
            CreatedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        };

        taskRepository.Add(task);

        // 会话内的一次生成 = 一对消息（用户需求 + 助手结果）。
        // 两者的 id 互指，前端既能顺着 pairMessageId 找配对消息，
        // 也能顺着 taskId 从任务反查它对应的那条助手消息（双击任务定位靠这条链路）。
        var nextSeq = await messageRepository.GetQueryable()
            .Where(x => x.ConversationId == targetConversation.Id)
            .Select(x => (int?)x.Seq)
            .MaxAsync(cancellationToken) ?? 0;

        var paramsJson = GenerationTaskSerialization.SerializeParams(
            ImageGenerationInputValidator.BuildParamsSnapshot(input));

        // 两条消息的雪花 id 先算出来互指，再各自入库
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
            AssetsJson = GenerationTaskSerialization.SerializeAssets(referenceImages),
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
            Text = referenceImages.Count > 0
                ? $"收到 {referenceImages.Count} 张参考图，正在按你的描述改这张图…"
                : "正在按你的描述生成…",
            TaskId = task.Id,
            PairMessageId = userMessageId.ToString(CultureInfo.InvariantCulture),
            AssetsJson = "[]",
            ParamsJson = paramsJson,
            Seq = nextSeq + 2,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // 空会话的类型由首个真实任务决定，与视频提交侧保持对称。
        if (targetConversation.TaskCount == 0)
            targetConversation.Type = GenerationTaskType.Image;

        targetConversation.MessageCount += 2;
        targetConversation.TaskCount += 1;
        targetConversation.UpdatedAt = now;

        // 首条消息进来时把占位标题换成提示词摘要，
        // 否则侧边栏会一排"新建创作"，用户根本分不清哪条是哪条。
        if (targetConversation.MessageCount == 2 && targetConversation.Title == "新建创作")
        {
            targetConversation.Title = GenerationTaskNaming.FromPrompt(input.Prompt);
        }

        // 只在「复用已有会话」时才需要显式 Update。
        //
        // 新建的会话在前面已经 Add 过（EF 状态为 Added），此时再调 Update()
        // 会把状态强改为 Modified —— EF 于是生成一条 UPDATE 而不是 INSERT，
        // 数据库里没有对应行，SaveChanges 抛
        // DbUpdateConcurrencyException「期望影响 1 行，实际 0 行」。
        //
        // 这里统一用「是否新建」这个已知事实来判断，比再查一次库可靠。
        if (!isNewConversation)
        {
            conversationRepository.Update(targetConversation);
        }

        await taskRepository.SaveChangesAsync(cancellationToken);

        try
        {
            await dispatcher.DispatchAsync(task, cancellationToken);
        }
        catch (Exception exception)
        {
            // 消息发不出去（比如 RabbitMQ 不可用）时任务会永远停在排队中，
            // 这里直接置为失败并把原因写给用户，避免出现"假排队"。
            logger.LogError(exception, "任务 {TaskId} 投递到消息队列失败", task.Id);

            task.Status = GenerationTaskStatus.Failed;
            task.ErrorMessage = $"任务投递到消息队列失败：{exception.Message}";
            task.FinishedAt = DateTimeOffset.UtcNow;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            taskRepository.Update(task);
            await taskRepository.SaveChangesAsync(cancellationToken);

            return Result.Failure("任务已创建，但投递到消息队列失败，请稍后重试。");
        }

        // ConversationId 必须回传：前端要用它把本次消息挂到会话上，
        // 否则会话虽然落库了，但前端不知道新会话的 id，历史消息不会出现在聊天记录里。
        return Result.Success(new ImageGenerationResultDto(
            task.Id.ToString(CultureInfo.InvariantCulture),
            task.Status.ToWireValue(),
            [],
            targetConversation.Id.ToString(CultureInfo.InvariantCulture)));
    }
}
