using System.Globalization;
using System.Text.Json.Nodes;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.VideoService.Delivery;

/// <summary>
/// 把中立的 <see cref="VideoDeliveryRequest" /> 组装成各家形状的请求体。
///
/// 这是「一份代码适配所有厂商」真正落地的地方：协议差异全部由
/// <see cref="VideoRequestFieldMap" /> 里的字段路径表达，
/// 本类只负责按路径写入 JsonObject，因此新增厂商不需要改这里的逻辑。
/// </summary>
public static class VideoRequestBuilder
{
    /// <summary>
    /// 构造带模式的提交请求体（全能 / 首尾帧 / 智能多帧）。
    ///
    /// <para>协议不支持数组形状时（百炼、OpenAI 等）会自动降级：
    /// 取出第一张图当首帧，其余模式信息忽略。这样老的视频模型在界面上
    /// 多出模式选择后依然能正常提交，不会因为"多传了它不认的字段"直接 400。</para>
    /// </summary>
    public static string BuildModeSubmitBody(VideoModeDeliveryRequest request, VideoDeliverySpec spec)
    {
        if (spec.BodyShape != VideoRequestBodyShape.ContentArray)
        {
            var firstImage = request.References
                .FirstOrDefault(x => x.Kind == VideoReferenceKind.Image)?.Url;

            var legacy = new VideoDeliveryRequest(
                request.Model,
                request.Prompt,
                request.NegativePrompt,
                firstImage,
                request.Resolution,
                request.DurationSeconds,
                Fps: null,
                request.Seed,
                request.ExtraParams);

            return BuildSubmitBody(legacy, spec);
        }

        return BuildContentArrayBody(request, spec);
    }

    /// <summary>
    /// 构造火山方舟 Seedance 那种 <c>content</c> 数组请求体。
    ///
    /// <code>
    /// { "model": "doubao-seedance-2-0-fast-260128",
    ///   "content": [ {"type":"text","text":"..."},
    ///                {"type":"image_url","role":"first_frame","image_url":{"url":"..."}},
    ///                {"type":"image_url","role":"last_frame","image_url":{"url":"..."}} ],
    ///   "resolution":"720p","ratio":"adaptive","duration":5,"seed":123 }
    /// </code>
    /// </summary>
    private static string BuildContentArrayBody(VideoModeDeliveryRequest request, VideoDeliverySpec spec)
    {
        var fields = spec.RequestFields;
        var body = new JsonObject();

        if (!string.IsNullOrWhiteSpace(request.Model.ModelKey))
            JsonNodePath.Set(body, fields.Model, JsonValue.Create(request.Model.ModelKey));

        // 提示词排在数组第一位，与官方示例一致
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = fields.TextItemType ?? "text",
                ["text"] = request.Prompt,
            },
        };

        var references = request.EffectiveReferences;
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            var (itemType, role) = ResolveItem(request.Mode, reference.Kind, index, fields);

            var item = new JsonObject
            {
                ["type"] = itemType,
                [itemType] = new JsonObject
                {
                    [fields.UrlField ?? "url"] = reference.Url,
                },
            };

            // role 为空表示"不声明角色"，比如某些厂商不需要区分参考图
            if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(fields.RoleField))
                item[fields.RoleField] = role;

            content.Add(item);
        }

        body[fields.ContentArray ?? "content"] = content;

        if (request.DurationSeconds is > 0)
            JsonNodePath.Set(body, fields.Duration, JsonValue.Create(request.DurationSeconds.Value));

        JsonNodePath.Set(body, fields.Seed, JsonValue.Create(request.EffectiveSeed));

        ApplyModeResolution(body, request, spec, fields);

        ApplyExtraParams(body, request.ExtraParams, fields.ExtraParamsContainer);

        return body.ToJsonString();
    }

    /// <summary>
    /// 决定每个素材项的 <c>type</c> 与 <c>role</c>。
    ///
    /// 首尾帧与全能参考在官方文档里是<b>互斥</b>的：首尾帧必须用
    /// <c>first_frame</c> / <c>last_frame</c>，不能写成 <c>reference_image</c>；
    /// 智能多帧走的是"多张参考图"，因此用 <c>reference_image</c>。
    /// </summary>
    private static (string ItemType, string? Role) ResolveItem(
        VideoGenerationMode mode,
        VideoReferenceKind kind,
        int index,
        VideoRequestFieldMap fields)
    {
        if (mode == VideoGenerationMode.FirstLastFrame)
        {
            var role = index == 0 ? fields.RoleFirstFrame : fields.RoleLastFrame;
            return (fields.ImageItemType ?? "image_url", role);
        }

        return kind switch
        {
            VideoReferenceKind.Video => (fields.VideoItemType ?? "video_url", fields.RoleReferenceVideo),
            VideoReferenceKind.Audio => (fields.AudioItemType ?? "audio_url", fields.RoleReferenceAudio),
            _ => (fields.ImageItemType ?? "image_url", fields.RoleReferenceImage),
        };
    }

    /// <summary>
    /// 数组形状下的分辨率与画幅。
    ///
    /// 与扁平方形的差别有两条，都是踩过就会失败的事实：
    /// <list type="number">
    ///   <item>Seedance 要<b>小写</b>档位（<c>720p</c>），传 <c>720P</c> 直接被拒；</item>
    ///   <item>首尾帧必须 <c>ratio=adaptive</c>，固定比例会异步报 TaskTypeConstraint。</item>
    /// </list>
    /// </summary>
    private static void ApplyModeResolution(
        JsonObject body,
        VideoModeDeliveryRequest request,
        VideoDeliverySpec spec,
        VideoRequestFieldMap fields)
    {
        var resolution = (request.Resolution ?? string.Empty).Trim();
        var capabilities = spec.Capabilities;

        if (spec.ResolutionStyle != VideoResolutionStyle.Omit
            && resolution.Length > 0
            && !string.IsNullOrWhiteSpace(fields.Resolution))
        {
            JsonNodePath.Set(body, fields.Resolution, JsonValue.Create(ToTierLower(resolution)));
        }

        if (string.IsNullOrWhiteSpace(fields.Ratio))
            return;

        // 首尾帧锁定输出画幅，官方要求 ratio 必须是 adaptive
        var forceAdaptive = capabilities?.FramesRequireAdaptiveRatio == true
                            && request.Mode == VideoGenerationMode.FirstLastFrame;

        if (forceAdaptive)
        {
            JsonNodePath.Set(body, fields.Ratio, JsonValue.Create("adaptive"));
            return;
        }

        if (resolution.Length > 0)
            JsonNodePath.Set(body, fields.Ratio, JsonValue.Create(ToRatio(resolution)));
    }

    /// <summary>像素尺寸或档位 → 小写档位（火山方舟要 720p 而不是 720P）</summary>
    private static string ToTierLower(string resolution)
    {
        var tier = ToTier(resolution);
        return tier.ToLowerInvariant();
    }

    /// <summary>构造提交任务的 JSON 请求体</summary>
    public static string BuildSubmitBody(VideoDeliveryRequest request, VideoDeliverySpec spec)
    {
        var fields = spec.RequestFields;
        var body = new JsonObject();

        // 模型名。没填 modelKey 时不传，避免下游拿到 null 模型名报一句看不懂的错误
        if (!string.IsNullOrWhiteSpace(request.Model.ModelKey))
            JsonNodePath.Set(body, fields.Model, JsonValue.Create(request.Model.ModelKey));

        JsonNodePath.Set(body, fields.Prompt, JsonValue.Create(request.Prompt));

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            JsonNodePath.Set(body, fields.NegativePrompt, JsonValue.Create(request.NegativePrompt));

        if (request.IsImageToVideo)
            JsonNodePath.Set(body, fields.FirstFrame, JsonValue.Create(request.FirstFrameUrl));

        if (request.DurationSeconds is > 0)
            JsonNodePath.Set(body, fields.Duration, JsonValue.Create(request.DurationSeconds.Value));

        JsonNodePath.Set(body, fields.Seed, JsonValue.Create(request.EffectiveSeed));

        ApplyResolution(body, fields, request, spec.ResolutionStyle);

        // 帧率：只有预设明确声明了字段才传。
        // 直接塞一个模型不认识的 fps 字段，轻则 400，重则被静默忽略让用户困惑。
        if (request.Fps is > 0 && !string.IsNullOrWhiteSpace(fields.Fps))
            JsonNodePath.Set(body, fields.Fps, JsonValue.Create(request.Fps.Value));

        ApplyExtraParams(body, request.ExtraParams, fields.ExtraParamsContainer);

        return body.ToJsonString();
    }

    /// <summary>
    /// 按预设要求的形式写分辨率。
    ///
    /// <c>1280x720</c> 与 <c>720P</c> 是两套完全不同的约定，
    /// 而前端表单里的「分辨率」是用户按 <c>1920x1080</c> 这样选的，
    /// 因此这里必须做一次从像素尺寸到档位的推导。
    /// </summary>
    private static void ApplyResolution(
        JsonObject body,
        VideoRequestFieldMap fields,
        VideoDeliveryRequest request,
        VideoResolutionStyle style)
    {
        if (style == VideoResolutionStyle.Omit || string.IsNullOrWhiteSpace(fields.Resolution))
            return;

        var resolution = (request.Resolution ?? string.Empty).Trim();
        if (resolution.Length == 0)
            return;

        switch (style)
        {
            case VideoResolutionStyle.PixelSize:
                JsonNodePath.Set(body, fields.Resolution, JsonValue.Create(NormalizePixelSize(resolution)));
                break;

            case VideoResolutionStyle.Tier:
                JsonNodePath.Set(body, fields.Resolution, JsonValue.Create(ToTier(resolution)));
                break;

            case VideoResolutionStyle.TierAndRatio:
                JsonNodePath.Set(body, fields.Resolution, JsonValue.Create(ToTier(resolution)));
                JsonNodePath.Set(body, fields.Ratio, JsonValue.Create(ToRatio(resolution)));
                break;
        }
    }

    /// <summary>把 1920x1080 统一成 1920x720 这类像素写法，顺带兼容用户填 1280*720 的情况</summary>
    private static string NormalizePixelSize(string resolution)
    {
        var normalized = resolution.Replace('*', 'x').Replace('X', 'x');

        return normalized.Contains('x') ? normalized : normalized + "x720";
    }

    /// <summary>
    /// 从像素尺寸推出百炼这类接口要的档位。
    ///
    /// 判据取<b>短边</b>而不是高度：竖屏的 <c>720x1280</c> 同样属于 720P，
    /// 若按高度判断会误判成 1080P，产出的清晰度与用户选择不符还不报错。
    /// </summary>
    private static string ToTier(string resolution)
    {
        var normalized = resolution.Replace('*', 'x').Replace('X', 'x');

        // 用户可能直接填了 720P / 1080P
        if (!normalized.Contains('x'))
            return normalized.ToUpperInvariant();

        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return "720P";
        }

        return Math.Min(width, height) >= 1080 ? "1080P" : "720P";
    }

    /// <summary>
    /// 从像素尺寸推出宽高比（1280x720 → 16:9）。
    ///
    /// 用户直接选档位（<c>480P</c> 这类没有 <c>x</c> 的写法）时返回 <c>adaptive</c>——
    /// 只给清晰度不给比例，就是在说"比例你看着办"，这正是万相 2.6 起 <c>ratio</c> 字段
    /// 支持 <c>adaptive</c> 的用意。硬塞一个 16:9 反而会把用户没表达的意图写死。
    /// </summary>
    private static string ToRatio(string resolution)
    {
        var normalized = resolution.Replace('*', 'x').Replace('X', 'x');

        if (!normalized.Contains('x'))
            return "adaptive";

        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return "16:9";
        }

        // 用公约数化简，避免 1280x720 被算成 128:72
        var divisor = GreatestCommonDivisor(width, height);

        return $"{width / divisor}:{height / divisor}";
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    /// <summary>
    /// 把模型扩展参数挂到指定容器下。
    ///
    /// 容器为空表示挂到请求体根（OpenAI / SiliconFlow 的做法），
    /// 否则先确保容器对象存在再逐个写入。
    /// </summary>
    private static void ApplyExtraParams(
        JsonObject body,
        IReadOnlyDictionary<string, object?> extraParams,
        string container)
    {
        if (extraParams is not { Count: > 0 })
            return;

        JsonObject target = body;

        if (!string.IsNullOrWhiteSpace(container))
        {
            if (body[container] is not JsonObject existing)
            {
                existing = new JsonObject();
                body[container] = existing;
            }

            target = existing;
        }

        foreach (var (key, value) in extraParams)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            target[key.Trim()] = ToJsonNode(value);
        }
    }

    /// <summary>把反序列化出来的弱类型值转成 JsonNode</summary>
    public static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            // 参数快照反序列化后多数是 JsonElement，这里原样回填以保留数字/字符串的类型
            System.Text.Json.JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonValue.Create(value.ToString()),
        };
    }
}