using System.Text.Json;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.VideoService.Delivery;
using WooVisAgentServices.VideoService.Dtos;
using WooVisAgentServices.VideoService.Validation;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 三种视频生成模式（全能 / 首尾帧 / 智能多帧）的请求体与校验规则。
///
/// 这层的价值在于「形状」：火山方舟的 content 数组一旦少一个字段或 role 写错，
/// 报错发生在<b>任务启动之后</b>（InvalidParameter.TaskTypeConstraint），
/// 用户要等几分钟才看到失败，排查成本极高。因此把请求体逐字段钉死在这里。
/// </summary>
public class VideoModeDeliveryTests
{
    private static ModelConfig ArkModel(string modelKey = "doubao-seedance-2-0-fast-260128") => new()
    {
        Id = 1,
        Name = "Seedance 2.0",
        OwnerId = "u1",
        Category = ModelCategory.Video,
        Provider = "VolcEngine",
        ModelKey = modelKey,
        Endpoint = "https://ark.cn-beijing.volces.com",
        Enabled = true,
    };

    private static VideoModeDeliveryRequest Request(
        VideoGenerationMode mode,
        params (VideoReferenceKind Kind, string Url)[] references) => new(
            ArkModel(),
            mode,
            "一只猫在雨夜街头走过积水",
            references.Select(x => new VideoReference(x.Kind, x.Url)).ToList(),
            null,
            "720P",
            5,
            null,
            new Dictionary<string, object?>());

    private static JsonElement Build(VideoModeDeliveryRequest request, VideoDeliverySpec spec)
        => JsonDocument.Parse(VideoRequestBuilder.BuildModeSubmitBody(request, spec)).RootElement;

    // ---- 请求体形状 --------------------------------------------------

    [Fact]
    public void 火山方舟域名应被识别为方舟预设()
    {
        var spec = VideoDeliveryResolver.Resolve(ArkModel());

        Assert.NotNull(spec);
        Assert.Equal("ark-seedance", spec!.PresetName);
        Assert.Equal(VideoRequestBodyShape.ContentArray, spec.BodyShape);
    }

    [Fact]
    public void 全能生成_图片视频音频分别映射成各自的role()
    {
        var body = Build(
            Request(
                VideoGenerationMode.Omni,
                (VideoReferenceKind.Image, "https://cdn/a.jpg"),
                (VideoReferenceKind.Image, "https://cdn/b.jpg"),
                (VideoReferenceKind.Video, "https://cdn/c.mp4"),
                (VideoReferenceKind.Audio, "https://cdn/d.mp3")),
            VideoDeliveryPresets.ArkSeedance);

        var content = body.GetProperty("content");
        Assert.Equal(5, content.GetArrayLength()); // 1 段文本 + 4 个素材

        var text = content[0];
        Assert.Equal("text", text.GetProperty("type").GetString());
        Assert.Equal("一只猫在雨夜街头走过积水", text.GetProperty("text").GetString());

        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("reference_image", content[1].GetProperty("role").GetString());
        Assert.Equal("https://cdn/a.jpg", content[1].GetProperty("image_url").GetProperty("url").GetString());

        // 第二张图也是参考图，不能因为"排在后面"就被当成别的角色
        Assert.Equal("reference_image", content[2].GetProperty("role").GetString());

        Assert.Equal("video_url", content[3].GetProperty("type").GetString());
        Assert.Equal("reference_video", content[3].GetProperty("role").GetString());

        Assert.Equal("audio_url", content[4].GetProperty("type").GetString());
        Assert.Equal("reference_audio", content[4].GetProperty("role").GetString());
    }

    [Fact]
    public void 全能生成_不传素材就是纯文生视频()
    {
        var body = Build(
            Request(VideoGenerationMode.Omni),
            VideoDeliveryPresets.ArkSeedance);

        var content = body.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public void 首尾帧_两张图分别用first_frame与last_frame()
    {
        var body = Build(
            Request(
                VideoGenerationMode.FirstLastFrame,
                (VideoReferenceKind.Image, "https://cdn/first.jpg"),
                (VideoReferenceKind.Image, "https://cdn/last.jpg")),
            VideoDeliveryPresets.ArkSeedance);

        var content = body.GetProperty("content");
        Assert.Equal("first_frame", content[1].GetProperty("role").GetString());
        Assert.Equal("last_frame", content[2].GetProperty("role").GetString());

        // 首尾帧与参考图互斥：素材项里绝不能出现 reference_image（文本项没有 role，跳过）
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("role", out var role))
                continue;

            Assert.NotEqual("reference_image", role.GetString());
        }
    }

    [Fact]
    public void 首尾帧_官方要求ratio必须是adaptive()
    {
        var body = Build(
            Request(
                VideoGenerationMode.FirstLastFrame,
                (VideoReferenceKind.Image, "https://cdn/a.jpg"),
                (VideoReferenceKind.Image, "https://cdn/b.jpg")),
            VideoDeliveryPresets.ArkSeedance);

        Assert.Equal("adaptive", body.GetProperty("ratio").GetString());
    }

    [Fact]
    public void 智能多帧_多张图都当作参考图且保持顺序()
    {
        var body = Build(
            Request(
                VideoGenerationMode.Keyframes,
                (VideoReferenceKind.Image, "https://cdn/1.jpg"),
                (VideoReferenceKind.Image, "https://cdn/2.jpg"),
                (VideoReferenceKind.Image, "https://cdn/3.jpg")),
            VideoDeliveryPresets.ArkSeedance);

        var content = body.GetProperty("content");
        Assert.Equal(4, content.GetArrayLength());
        Assert.Equal("https://cdn/1.jpg", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("https://cdn/3.jpg", content[3].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void 方舟的分辨率必须是小写档位()
    {
        var body = Build(Request(VideoGenerationMode.Omni), VideoDeliveryPresets.ArkSeedance);

        // 传 720P 被方舟直接拒；百炼要 720P，方舟要 720p
        Assert.Equal("720p", body.GetProperty("resolution").GetString());
    }

    [Fact]
    public void 方舟请求体里不应出现负向提示词与帧率字段()
    {
        var body = Build(Request(VideoGenerationMode.Omni), VideoDeliveryPresets.ArkSeedance);

        // Seedance 没有这两个字段，多传会被拒
        Assert.False(body.TryGetProperty("negative_prompt", out _));
        Assert.False(body.TryGetProperty("fps", out _));
        Assert.Equal(5, body.GetProperty("duration").GetInt32());
    }

    [Fact]
    public void 模式必须以omni这类字符串出入JSON而不是数字()
    {
        // 踩过的坑：接口把能力声明吐成 [1,2,3]，前端拿到三个数字对不上下拉选项，
        // 而 C# 侧的断言全都不会暴露这个问题——只有序列化结果才算证据
        var json = JsonSerializer.Serialize(new VideoCapabilitySpec
        {
            Modes = [VideoGenerationMode.Omni, VideoGenerationMode.FirstLastFrame, VideoGenerationMode.Keyframes],
        });

        Assert.Contains("\"omni\"", json);
        Assert.Contains("\"firstLastFrame\"", json);
        Assert.Contains("\"keyframes\"", json);

        var parsed = JsonSerializer.Deserialize<VideoCapabilitySpec>(json);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Modes.Count);
        Assert.Equal(VideoGenerationMode.Keyframes, parsed.Modes[2]);
    }

    [Fact]
    public void 读取时兼容早期落库的数字写法()
    {
        // 交付模板的 JSON 是 camelCase 落库的，这里用同样的大小写策略读回来
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<VideoCapabilitySpec>("""{"modes":[1,3]}""", options);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Modes.Count);
        Assert.Equal(VideoGenerationMode.Omni, parsed.Modes[0]);
        Assert.Equal(VideoGenerationMode.Keyframes, parsed.Modes[1]);
    }

    [Fact]
    public void 扁平协议会自动降级成单首帧不影响既有模型()
    {
        var model = new ModelConfig
        {
            Id = 2,
            Name = "万相视频",
            OwnerId = "u1",
            Category = ModelCategory.Video,
            Provider = "AliyunBailian",
            ModelKey = "wan2.6-t2v-preview",
            Endpoint = "https://dashscope.aliyuncs.com",
            Enabled = true,
        };

        var spec = VideoDeliveryResolver.Resolve(model);
        Assert.NotNull(spec);
        Assert.Equal(VideoRequestBodyShape.Flat, spec!.BodyShape);

        var request = new VideoModeDeliveryRequest(
            model,
            VideoGenerationMode.Omni,
            "一只猫走过积水",
            [new VideoReference(VideoReferenceKind.Image, "https://cdn/a.jpg")],
            null,
            "720P",
            5,
            null,
            new Dictionary<string, object?>());

        var body = Build(request, spec);

        // 降级后仍然是百炼那套字段名
        Assert.Equal("一只猫走过积水", body.GetProperty("input").GetProperty("prompt").GetString());
        Assert.Equal("https://cdn/a.jpg", body.GetProperty("input").GetProperty("img_url").GetString());
        Assert.Equal("720P", body.GetProperty("parameters").GetProperty("resolution").GetString());
    }

    // ---- 校验规则 ----------------------------------------------------

    private static VideoGenerationInput Input(VideoGenerationMode mode, params (string Kind, string Url)[] refs) => new()
    {
        ModelId = "1",
        Prompt = "测试提示词",
        Mode = mode.ToWireValue(),
        References = refs.Select(x => new VideoReferenceInput { Kind = x.Kind, Url = x.Url }).ToList(),
    };

    private static string? Check(VideoGenerationInput input)
        => VideoGenerationInputValidator.ValidateModeAndCapabilities(
            input, ArkModel(), VideoDeliveryPresets.ArkSeedance.Capabilities);

    [Fact]
    public void 首尾帧必须恰好两张图()
    {
        var one = Check(Input(VideoGenerationMode.FirstLastFrame, ("image", "https://cdn/a.jpg")));
        Assert.NotNull(one);
        Assert.Contains("恰好两张", one);

        var three = Check(Input(
            VideoGenerationMode.FirstLastFrame,
            ("image", "https://cdn/a.jpg"),
            ("image", "https://cdn/b.jpg"),
            ("image", "https://cdn/c.jpg")));
        Assert.NotNull(three);

        var ok = Check(Input(
            VideoGenerationMode.FirstLastFrame,
            ("image", "https://cdn/a.jpg"),
            ("image", "https://cdn/b.jpg")));
        Assert.Null(ok);
    }

    [Fact]
    public void 首尾帧不能混入视频或音频()
    {
        var error = Check(Input(
            VideoGenerationMode.FirstLastFrame,
            ("image", "https://cdn/a.jpg"),
            ("video", "https://cdn/b.mp4")));

        Assert.NotNull(error);
        Assert.Contains("只支持图片", error);
    }

    [Fact]
    public void 智能多帧至少两张且不超过上限()
    {
        Assert.NotNull(Check(Input(VideoGenerationMode.Keyframes, ("image", "https://cdn/a.jpg"))));

        var tooMany = Check(Input(
            VideoGenerationMode.Keyframes,
            Enumerable.Range(0, 11).Select(i => ("image", $"https://cdn/{i}.jpg")).ToArray()));
        Assert.NotNull(tooMany);
        Assert.Contains("最多支持 10 张", tooMany);
    }

    [Fact]
    public void 全能生成_超出各类素材上限时报出是哪一类()
    {
        var images = Enumerable.Range(0, 10).Select(i => ("image", $"https://cdn/{i}.jpg")).ToArray();
        var error = Check(Input(VideoGenerationMode.Omni, images));

        Assert.NotNull(error);
        Assert.Contains("图片参考素材最多 9 个", error);

        var audios = new[] { ("audio", "https://cdn/a.mp3"), ("audio", "https://cdn/b.mp3"), ("audio", "https://cdn/c.mp3"), ("audio", "https://cdn/d.mp3") };
        Assert.Contains("音频参考素材最多 3 个", Check(Input(VideoGenerationMode.Omni, audios)));
    }

    [Fact]
    public void 模型不支持的模式要在提交前就被拦下()
    {
        var onlyFrames = new VideoCapabilitySpec
        {
            Modes = [VideoGenerationMode.Omni, VideoGenerationMode.FirstLastFrame],
            MaxReferenceImages = 1,
        };

        var error = VideoGenerationInputValidator.ValidateModeAndCapabilities(
            Input(VideoGenerationMode.Keyframes, ("image", "https://cdn/a.jpg"), ("image", "https://cdn/b.jpg")),
            ArkModel(),
            onlyFrames);

        Assert.NotNull(error);
        Assert.Contains("不支持「智能多帧」", error);
        Assert.Contains("全能生成", error); // 顺便告诉用户还能选什么
    }

    [Fact]
    public void 老字段firstFrameUrl能继续当作一张参考图()
    {
        var input = new VideoGenerationInput
        {
            ModelId = "1",
            Prompt = "测试",
            FirstFrameUrl = "/uploads/2026/10/18/a.jpg",
        };

        var references = VideoGenerationInputValidator.ResolveReferences(input);

        Assert.Single(references);
        Assert.Equal(VideoReferenceKind.Image, references[0].Kind);
        Assert.Equal("/uploads/2026/10/18/a.jpg", references[0].Url);
    }

    [Fact]
    public void 素材上限可以由模型配置覆盖()
    {
        // Seedance 2.5 比 2.0 宽松，靠 capabilities 覆盖而不改预设
        var generous = new VideoCapabilitySpec
        {
            Modes = [VideoGenerationMode.Omni, VideoGenerationMode.FirstLastFrame, VideoGenerationMode.Keyframes],
            MaxReferenceImages = 30,
            MaxReferenceVideos = 10,
            MaxReferenceAudios = 10,
            MaxKeyframes = 10,
        };

        var input = Input(
            VideoGenerationMode.Omni,
            Enumerable.Range(0, 20).Select(i => ("image", $"https://cdn/{i}.jpg")).ToArray());

        Assert.Null(VideoGenerationInputValidator.ValidateModeAndCapabilities(
            input, ArkModel(), generous));
    }
}