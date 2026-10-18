using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 内置的视频交付预设。
///
/// 每个预设都是<b>纯数据</b>，描述一家（或一类）接口的形状。
/// 新增厂商如果不在这几个预设里，只需要在模型配置里改写模板，
/// 不必往这里加代码、加类、发版。
/// </summary>
public static class VideoDeliveryPresets
{
    /// <summary>
    /// OpenAI 风格：<c>POST /v1/videos</c> 提交，返回 <c>id</c>，
    /// <c>GET /v1/videos/{id}</c> 轮询，<c>status=completed</c> 表示完成，
    /// 最后要<b>另外调 <c>/v1/videos/{id}/content</c> 下载二进制</b>——响应体里从来没有视频 URL。
    ///
    /// 这也是目前各大网关做「OpenAI 兼容」时最常对齐的形状，
    /// 因此它是自定义配置的<b>默认预设</b>：endpoint 填 OpenAI 或某个兼容网关时，
    /// 不配任何模板也能直接跑。
    /// </summary>
    public static VideoDeliverySpec OpenAiVideos { get; } = new()
    {
        PresetName = "openai-videos",
        SubmitPath = "/v1/videos",
        StatusPath = "/v1/videos/{taskId}",
        StatusMethod = "GET",
        TaskIdPath = "id",
        StatusValuePath = "status",
        SucceededStatus = "completed",
        FailedStatuses = "failed",
        VideoUrlPath = null,
        ErrorPath = "error.message",
        OutputKind = VideoOutputKind.ContentBinary,
        ContentPath = "/v1/videos/{taskId}/content",
        RequestFields = new VideoRequestFieldMap
        {
            Model = "model",
            Prompt = "prompt",
            NegativePrompt = null,
            FirstFrame = "input_reference",
            Duration = "seconds",
            Resolution = "size",
            Ratio = null,
            Fps = null,
            Seed = null,
            ExtraParamsContainer = "",
        },
        ResolutionStyle = VideoResolutionStyle.PixelSize,
        // OpenAI 协议本身就是「兼容协议」：/v1/videos 必须跟在 endpoint 后面，
        // endpoint 里的前缀（自建网关的 /proxy/v1 等）是路径的一部分，不能丢。
        VendorNativePaths = false,
    };

    /// <summary>
    /// 阿里云百炼：<c>POST /api/v1/services/aigc/video-generation/video-synthesis</c>，
    /// 必须带 <c>X-DashScope-Async: enable</c>，任务号在 <c>output.task_id</c>，
    /// <c>GET /api/v1/tasks/{task_id}</c> 轮询，<c>output.task_status=SUCCEEDED</c> 完成，
    /// 视频地址在 <c>output.video_url</c>。
    ///
    /// 两个容易踩的点写在这里：
    /// <list type="number">
    ///   <item><b>文生视频与图生视频共用同一个路径</b>，靠 <c>model</c> 名区分（t2v / i2v），不是靠 URL；</item>
    ///   <item>首帧字段是 <c>input.img_url</c>，不是 <c>image_url</c> 也不是 <c>first_frame</c>；</item>
    /// </list>
    /// 另：<c>video_url</c> 只有 24 小时有效期，拿到后必须立刻转存。
    /// </summary>
    public static VideoDeliverySpec DashScopeVideo { get; } = new()
    {
        PresetName = "dashscope-video",
        SubmitPath = "/api/v1/services/aigc/video-generation/video-synthesis",
        StatusPath = "/api/v1/tasks/{taskId}",
        StatusMethod = "GET",
        TaskIdPath = "output.task_id",
        StatusValuePath = "output.task_status",
        SucceededStatus = "SUCCEEDED",
        FailedStatuses = "FAILED,CANCELED,UNKNOWN",
        VideoUrlPath = "output.video_url",
        ErrorPath = "output.message",
        OutputKind = VideoOutputKind.Url,
        ContentPath = null,
        ExtraHeaders = new Dictionary<string, string>
        {
            ["X-DashScope-Async"] = "enable",
        },
        RequestFields = new VideoRequestFieldMap
        {
            Model = "model",
            Prompt = "input.prompt",
            NegativePrompt = "input.negative_prompt",
            FirstFrame = "input.img_url",
            Duration = "parameters.duration",
            Resolution = "parameters.resolution",
            Ratio = "parameters.ratio",
            Fps = null,
            Seed = "parameters.seed",
            ExtraParamsContainer = "parameters",
        },
        ResolutionStyle = VideoResolutionStyle.TierAndRatio,
    };

    /// <summary>
    /// SiliconFlow 风格：<c>POST /v1/video/submit</c> 只返回 <c>requestId</c>，
    /// 轮询要 <b>POST</b> 到固定地址 <c>/v1/video/status</c> 并在 body 里传
    /// <c>{"requestId": "..."}</c>，状态值是 <c>Succeed</c>，视频地址在
    /// <c>results.videos[0].url</c>。
    ///
    /// 它是「轮询用 POST」这件事的现实样本——这也正是把 StatusMethod 做成可配置的原因。
    /// </summary>
    public static VideoDeliverySpec SiliconFlowVideo { get; } = new()
    {
        PresetName = "siliconflow-video",
        SubmitPath = "/v1/video/submit",
        StatusPath = "/v1/video/status",
        StatusMethod = "POST",
        StatusIdField = "requestId",
        TaskIdPath = "requestId",
        StatusValuePath = "status",
        SucceededStatus = "Succeed",
        FailedStatuses = "Failed,Expired",
        VideoUrlPath = "results.videos[0].url",
        ErrorPath = "message",
        OutputKind = VideoOutputKind.Url,
        ContentPath = null,
        RequestFields = new VideoRequestFieldMap
        {
            Model = "model",
            Prompt = "prompt",
            NegativePrompt = null,
            FirstFrame = "image",
            Duration = null,
            Resolution = "image_size",
            Ratio = null,
            Fps = null,
            Seed = "seed",
            ExtraParamsContainer = "",
        },
        // 该平台按首帧图比例自动匹配分辨率，不传 size
        ResolutionStyle = VideoResolutionStyle.Omit,
    };

    /// <summary>
    /// 阿里云百炼的<b>旧版</b>视频入口（<c>video-synthesis</c> 之外的 <c>text2video</c> / <c>image2video</c> 家族）。
    ///
    /// 与新版相比字段结构完全一致，差别只在路径，因此单独列出来供旧模型使用。
    /// </summary>
    public static VideoDeliverySpec DashScopeLegacyVideo { get; } = new()
    {
        PresetName = "dashscope-legacy-video",
        SubmitPath = "/api/v1/services/aigc/video-generation/video-synthesis",
        StatusPath = "/api/v1/tasks/{taskId}",
        StatusMethod = "GET",
        TaskIdPath = "output.task_id",
        StatusValuePath = "output.task_status",
        SucceededStatus = "SUCCEEDED",
        FailedStatuses = "FAILED,CANCELED,UNKNOWN",
        VideoUrlPath = "output.video_url",
        ErrorPath = "output.message",
        OutputKind = VideoOutputKind.Url,
        ExtraHeaders = new Dictionary<string, string>
        {
            ["X-DashScope-Async"] = "enable",
        },
        RequestFields = new VideoRequestFieldMap
        {
            Model = "model",
            Prompt = "input.prompt",
            NegativePrompt = "input.negative_prompt",
            FirstFrame = "input.img_url",
            Duration = "parameters.duration",
            Resolution = "parameters.size",
            Ratio = null,
            Fps = null,
            Seed = "parameters.seed",
            ExtraParamsContainer = "parameters",
        },
        // 旧版用 size="1280*720" 这种星号写法
        ResolutionStyle = VideoResolutionStyle.PixelSize,
    };

    /// <summary>
    /// 火山方舟 Seedance（即梦的 API 形态）：
    /// <c>POST /api/v3/contents/generations/tasks</c>，
    /// <c>GET /api/v3/contents/generations/tasks/{id}</c> 轮询，
    /// <c>status=succeeded</c> 完成，视频地址在 <c>content.video_url</c>。
    ///
    /// <para><b>它和其他预设的根本差别</b>：请求体不是扁平字段，而是一个 <c>content</c> 数组，
    /// 提示词和所有素材混在一起，靠 <c>type</c> 与 <c>role</c> 区分：</para>
    /// <code>
    /// { "model": "doubao-seedance-2-0-fast-260128",
    ///   "content": [
    ///     {"type": "text", "text": "提示词"},
    ///     {"type": "image_url", "role": "first_frame", "image_url": {"url": "..."}},
    ///     {"type": "image_url", "role": "last_frame",  "image_url": {"url": "..."}},
    ///     {"type": "image_url", "role": "reference_image", "image_url": {"url": "..."}},
    ///     {"type": "video_url", "role": "reference_video", "video_url": {"url": "..."}},
    ///     {"type": "audio_url", "role": "reference_audio", "audio_url": {"url": "..."}} ],
    ///   "resolution": "720p", "ratio": "adaptive", "duration": 5 }
    /// </code>
    ///
    /// <para>三个必须记住的约束（官方文档明确写了）：</para>
    /// <list type="number">
    ///   <item><b>首帧、首尾帧、全模态参考三种场景互斥</b>，不能混在一次请求里；</item>
    ///   <item><b>首尾帧时 <c>ratio</c> 必须为 <c>adaptive</c></b>，否则异步报错 TaskTypeConstraint；</item>
    ///   <item><b>分辨率是小写 <c>720p</c></b>，传大写 720P 直接被拒。</item>
    /// </list>
    ///
    /// <para>能力上限按<b>保守值</b>给（9 图 / 3 视频 / 3 音频，对应 Seedance 2.0 系列）：
    /// Seedance 2.5 可以到 30 图 / 10 视频 / 10 音频，但那是文档里的版本差异，
    /// 用户可以在模型配置的交付模板里改 <c>capabilities</c> 对象覆盖。</para>
    /// </summary>
    public static VideoDeliverySpec ArkSeedance { get; } = new()
    {
        PresetName = "ark-seedance",
        SubmitPath = "/api/v3/contents/generations/tasks",
        StatusPath = "/api/v3/contents/generations/tasks/{taskId}",
        StatusMethod = "GET",
        TaskIdPath = "id",
        StatusValuePath = "status",
        SucceededStatus = "succeeded",
        // expired 是 Seedance 特有的一种失败（任务超时过期），漏掉它会让任务一直轮询到超时
        FailedStatuses = "failed,cancelled,canceled,expired",
        VideoUrlPath = "content.video_url",
        ErrorPath = "error.message",
        OutputKind = VideoOutputKind.Url,
        ContentPath = null,
        ExtraHeaders = new Dictionary<string, string>(),
        BodyShape = VideoRequestBodyShape.ContentArray,
        RequestFields = new VideoRequestFieldMap
        {
            Model = "model",
            // 提示词也走 content 数组，这里留空表示"不按路径写"，由数组分支负责
            Prompt = null,
            // Seedance 没有负向提示词这个字段
            NegativePrompt = null,
            FirstFrame = null,
            Duration = "duration",
            Resolution = "resolution",
            Ratio = "ratio",
            Fps = null,
            Seed = "seed",
            ExtraParamsContainer = "",
        },
        ResolutionStyle = VideoResolutionStyle.TierLower,
        Capabilities = new VideoCapabilitySpec
        {
            Modes = [VideoGenerationMode.Omni, VideoGenerationMode.FirstLastFrame, VideoGenerationMode.Keyframes],
            MaxReferenceImages = 9,
            MaxReferenceVideos = 3,
            MaxReferenceAudios = 3,
            MaxKeyframes = 10,
            FramesRequireAdaptiveRatio = true,
        },
    };

    /// <summary>全部预设，供「选哪个预设」的界面下拉使用</summary>
    public static IReadOnlyList<VideoDeliverySpec> All { get; } =
    [
        OpenAiVideos,
        DashScopeVideo,
        DashScopeLegacyVideo,
        SiliconFlowVideo,
        ArkSeedance,
    ];

    /// <summary>按名称找预设；找不到返回 null（此时应当由用户的自定义模板接管）</summary>
    public static VideoDeliverySpec? Find(string? presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return null;

        var normalized = presetName.Trim();

        return All.FirstOrDefault(x =>
            string.Equals(x.PresetName, normalized, StringComparison.OrdinalIgnoreCase));
    }
}