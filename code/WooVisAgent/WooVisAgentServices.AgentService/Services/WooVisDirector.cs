using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.AgentService.Dtos;
using WooVisAgentServices.AgentService.Options;
using WooVisAgentServices.AgentService.Support;

namespace WooVisAgentServices.AgentService.Services;

/// <summary>
/// Agent 的决策层。**只产出建议，从不提交生成任务**——
/// 真正的提交仍然走 <c>SubmitImageGeneration</c> / <c>SubmitVideoGeneration</c>，
/// 由用户在页面上确认后触发。这条边界是刻意的：
/// 模型幻觉最多导致「建议不靠谱」，不会变成「凭空花钱」。
/// </summary>
public interface IWooVisDirector
{
    Task<AgentOutcome<PromptPolishDto>> PolishAsync(
        string ownerId,
        PromptPolishRequest request,
        CancellationToken cancellationToken);

    Task<AgentOutcome<VideoPlanDto>> PlanVideoAsync(
        string ownerId,
        VideoPlanRequest request,
        CancellationToken cancellationToken);
}

/// <summary>统一的成功/失败包装。失败信息是可以直接展示给用户的中文</summary>
public sealed record AgentOutcome<T>(T? Value, string? Error)
{
    public bool IsSuccess => Value is not null;

    public static AgentOutcome<T> Ok(T value) => new(value, null);

    public static AgentOutcome<T> Fail(string error) => new(default, error);
}

public class WooVisDirector(
    ILlmClientResolver resolver,
    IRepository<ModelConfig> repository,
    AgentOptions options,
    ILoggerFactory loggerFactory,
    ILogger<WooVisDirector> logger) : IWooVisDirector
{
    private const string DirectorName = "WooVisDirector";

    private static readonly string[] ResolutionOptions = ["480P", "720P", "1080P"];
    private static readonly string[] RatioOptions = ["16:9", "9:16", "1:1", "4:3", "3:4", "adaptive"];

    public async Task<AgentOutcome<PromptPolishDto>> PolishAsync(
        string ownerId,
        PromptPolishRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0)
            return AgentOutcome<PromptPolishDto>.Fail("提示词不能为空。");

        if (prompt.Length > options.MaxInputChars)
            return AgentOutcome<PromptPolishDto>.Fail($"提示词过长（{prompt.Length} 字），请控制在 {options.MaxInputChars} 字以内。");

        var target = string.Equals(request.Target, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        var style = string.IsNullOrWhiteSpace(request.Style) ? null : request.Style.Trim();

        var instructions = $$"""
            你是 AI {{(target == "video" ? "视频" : "图片")}}生成提示词的编辑。用户会给你一句口语化的描述，你把它改写成模型更容易理解的提示词。

            规则：
            1. 保留用户原意与所有具体细节（主体、动作、场景、颜色、风格、文字），不要替换成别的主体。
            2. 不要编造用户没提到的具体品牌、人名、地名。
            3. 可补充对生成有帮助的信息：{{(target == "video" ? "镜头运动、时长感、光线、画面稳定性" : "构图、镜头、光线、材质、画质")}}。
            4. 控制在 150 字以内，语言与用户输入保持一致（中文输入就输出中文）。
            {{(style is null ? string.Empty : $"5. 用户希望的整体风格是「{style}」，请贯彻到提示词里，但不要扩写成一段剧情。")}}

            只输出一个 JSON 对象，不要 Markdown 代码块，不要任何解释文字。格式：
            {"polished":"改写后的提示词","negative":"建议的负向提示词，没有就空字符串","tips":["最多3条、每条不超过20字的建议"]}
            """;

        var resolution = await resolver.ResolveAsync(ownerId, cancellationToken);
        if (!resolution.IsAvailable)
            return AgentOutcome<PromptPolishDto>.Fail(resolution.FailureReason ?? "语言模型不可用。");

        var (text, error) = await RunAsync(resolution.Client!, instructions, prompt, tools: [], cancellationToken);
        if (text is null)
            return AgentOutcome<PromptPolishDto>.Fail(error ?? "语言模型没有返回内容。");

        var dto = ParsePolish(prompt, text);

        // 取到的是 JSON 而不是提示词，说明模型的包裹方式又变了，把原文留下来
        if (dto.Polished.StartsWith('{'))
            logger.LogWarning("润色返回未解包，原文（前 300 字）：{Raw}", Truncate(text, 300));

        logger.LogInformation("提示词润色完成，原长 {Before}，新长 {After}", prompt.Length, dto.Polished.Length);
        return AgentOutcome<PromptPolishDto>.Ok(dto);
    }

    public async Task<AgentOutcome<VideoPlanDto>> PlanVideoAsync(
        string ownerId,
        VideoPlanRequest request,
        CancellationToken cancellationToken)
    {
        var idea = (request.Idea ?? string.Empty).Trim();
        if (idea.Length == 0)
            return AgentOutcome<VideoPlanDto>.Fail("请先描述你想要的视频内容。");

        if (idea.Length > options.MaxInputChars)
            return AgentOutcome<VideoPlanDto>.Fail($"描述过长（{idea.Length} 字），请控制在 {options.MaxInputChars} 字以内。");

        var models = await repository.GetListAsync(
            x => x.OwnerId == ownerId && x.Category == ModelCategory.Video && x.Enabled,
            cancellationToken);

        if (models.Count == 0)
            return AgentOutcome<VideoPlanDto>.Fail("还没有可用的视频模型。请先到「模型设置」新增并启用一个视频模型。");

        var catalog = models
            .OrderByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                id = x.Id.ToString(),
                name = x.Name,
                modelKey = x.ModelKey,
                textToVideo = x.SupportsTextToVideo,
                imageToVideo = x.SupportsImageToVideo,
            })
            .ToList();

        var mode = string.Equals(request.Mode, "image", StringComparison.OrdinalIgnoreCase) ? "image" : "text";

        var instructions = $$"""
            你是视频生成参数助手。用户给一个想法，你输出可以直接提交的参数建议。

            当前用户已启用的视频模型（modelId 只能从这里选，不要编造）：
            {{JsonSerializer.Serialize(catalog)}}

            规则：
            1. 生成一段适合视频模型的提示词，写清主体、动作、场景、镜头运动、光线，200 字以内，语言与用户输入一致。
            2. 只能推荐上面清单里存在的 modelId；若都不合适就留空字符串。
            {{(mode == "image" ? "3. 用户打算用图生视频：提示词要描述「这张图接下来怎么动」，不要重新描述画面主体。" : "3. 用户打算用文生视频：提示词需要自包含地描述整个画面。")}}
            4. 分辨率只能取 480P、720P、1080P；画幅只能取 16:9、9:16、1:1、adaptive。
            5. 时长取 3 到 10 之间的整数秒。

            只输出一个 JSON 对象，不要 Markdown 代码块，不要任何解释。格式：
            {"prompt":"提示词","modelId":"模型配置Id","durationSeconds":5,"resolution":"720P","ratio":"16:9","notes":["最多2条、每条不超过30字的说明"]}

            严格要求：顶层就是这个对象本身，不要把整个 JSON 再包成字符串，也不要把 JSON 塞进 prompt 字段；
            prompt 字段里只放给生成模型看的画面描述。
            """;

        var resolution = await resolver.ResolveAsync(ownerId, cancellationToken);
        if (!resolution.IsAvailable)
            return AgentOutcome<VideoPlanDto>.Fail(resolution.FailureReason ?? "语言模型不可用。");

        IList<AITool> tools = options.EnableToolCalling
            ? [AIFunctionFactory.Create(
                () => JsonSerializer.Serialize(catalog),
                name: "list_available_video_models",
                description: "列出当前账号已启用的视频模型配置（id、名称、模型标识、是否支持文生/图生）。推荐模型前可先调用它确认。")]
            : [];

        var (text, error) = await RunAsync(resolution.Client!, instructions, idea, tools, cancellationToken);
        if (text is null)
            return AgentOutcome<VideoPlanDto>.Fail(error ?? "语言模型没有返回内容。");

        var dto = ParsePlan(text, models);

        // 解析失败时把原文留下来：不同模型的包裹方式差别很大，
        // 没有原文就只能靠猜（这一条帮我们定位过「整段 JSON 被包进 prompt 字段」）
        if (dto.ModelId is null && dto.Notes.Count > 0 && dto.Notes[0].StartsWith("模型未按结构返回"))
            logger.LogWarning("视频规划返回无法解析为 JSON，原文（前 300 字）：{Raw}", Truncate(text, 300));

        return AgentOutcome<VideoPlanDto>.Ok(dto);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 跑一次 Agent。带一次「去掉工具重试」：
    /// 部分自建中转不支持 function calling，声明工具本身就会 400，
    /// 这时降级成纯对话仍能给出可用结果，而不是把错误抛给用户。
    /// </summary>
    private async Task<(string? Text, string? Error)> RunAsync(
        IChatClient chatClient,
        string instructions,
        string userText,
        IList<AITool>? tools,
        CancellationToken cancellationToken)
    {
        IList<AITool> attemptTools = tools ?? [];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var agent = new ChatClientAgent(
                    chatClient,
                    instructions,
                    DirectorName,
                    "WooVisAgent 的内容编排助手",
                    attemptTools.Count == 0 ? null : attemptTools,
                    loggerFactory,
                    services: null);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 300) + 10));

                var response = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, userText)],
                    cancellationToken: timeoutCts.Token);

                var text = response.Text;
                return string.IsNullOrWhiteSpace(text)
                    ? (null, "语言模型返回了空内容。")
                    : (text, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, $"调用语言模型超时（超过 {options.TimeoutSeconds} 秒），请稍后重试。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt == 0 && attemptTools.Count > 0)
            {
                // 只在「带了工具」的第一次失败时退一步重试，避免把鉴权失败也重试一遍
                logger.LogWarning(ex, "带工具调用失败，去掉工具重试一次");
                attemptTools = [];
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "调用语言模型失败");
                return (null, $"调用语言模型失败：{Summarize(ex)}");
            }
        }
    }

    private static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 200 ? message : message[..200] + "…";
    }

    private static PromptPolishDto ParsePolish(string original, string raw)
    {
        // 逐字段取值，不要求整段 JSON 合法：模型写出的长中文里夹一个未转义字符，
        // 严格解析就会整段失败，而按 key 定位仍能拿到可用结果
        var text = AgentJson.Unwrap(raw, "polished");

        var polished = AgentJson.ExtractString(text, "polished") ?? text.Trim();
        var negative = AgentJson.ExtractString(text, "negative");
        var tips = AgentJson.ExtractStringArray(text, "tips").Take(3).ToList();

        return new PromptPolishDto(original, polished.Trim(), negative, tips);
    }

    private static VideoPlanDto ParsePlan(string raw, IReadOnlyList<ModelConfig> models)
    {
        var notes = new List<string>();
        var text = AgentJson.Unwrap(raw, "prompt");

        var prompt = AgentJson.ExtractString(text, "prompt") ?? text.Trim();

        var modelId = AgentJson.ExtractString(text, "modelId");
        var model = MatchModel(modelId, models);
        if (model is null && !string.IsNullOrWhiteSpace(modelId))
            notes.Add("建议的模型不在你的配置里，已忽略。");

        var duration = AgentJson.ExtractInt(text, "durationSeconds");
        if (duration is < 1 or > 30)
            duration = null;

        var resolution = NormalizeOption(AgentJson.ExtractString(text, "resolution"), ResolutionOptions);
        var ratio = NormalizeOption(AgentJson.ExtractString(text, "ratio"), RatioOptions);

        // 一个结构化字段都没拿到，说明模型完全没按格式走
        if (modelId is null && duration is null && resolution is null && ratio is null)
            notes.Insert(0, "模型未按结构返回，参数请手动确认。");

        notes.AddRange(AgentJson.ExtractStringArray(text, "notes").Take(2));

        return new VideoPlanDto(
            prompt.Trim(),
            model?.Id.ToString(),
            model?.Name,
            duration,
            resolution,
            ratio,
            notes);
    }

    private static ModelConfig? MatchModel(string? modelId, IReadOnlyList<ModelConfig> models)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var byId = models.FirstOrDefault(x => string.Equals(x.Id.ToString(), modelId.Trim(), StringComparison.Ordinal));
        if (byId is not null)
            return byId;

        // 模型偶尔会输出模型标识或展示名而不是 Id，两种都兼容
        return models.FirstOrDefault(x => string.Equals(x.ModelKey, modelId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(x => string.Equals(x.Name, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeOption(string? value, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // 大小写不敏感匹配（模型常写成 720p / 16:9 与 16：9 混用，后半段处理全角冒号）
        var normalized = value.Trim().Replace('：', ':');
        return allowed.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
