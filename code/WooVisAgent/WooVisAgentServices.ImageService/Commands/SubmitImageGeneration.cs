using System.Globalization;
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
            CreatedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        };

        taskRepository.Add(task);
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

        return Result.Success(new ImageGenerationResultDto(
            task.Id.ToString(CultureInfo.InvariantCulture),
            task.Status.ToWireValue(),
            []));
    }
}
