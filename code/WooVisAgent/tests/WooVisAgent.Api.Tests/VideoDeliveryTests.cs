using System.Text.Json;
using System.Text.Json.Nodes;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.VideoService.Delivery;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 视频交付的纯逻辑测试。
///
/// 这里只测「拼出来的请求体对不对」「字段路径读不读得到值」——
/// 视频接口的差异全部落在这两件事上：
/// 拼错字段名或读错层级，表现都是"提交成功但拿不到视频"，
/// 而这类问题不写断言就只能等真实调用时才暴露。
/// </summary>
public class VideoDeliveryTests
{
    private static ModelConfig CreateModel(
        string endpoint = "https://dashscope.aliyuncs.com/api/v1",
        string? modelKey = "wan2.6-i2v-flash",
        string deliveryJson = "{}",
        string provider = "阿里云百炼")
        => new()
        {
            Id = 1,
            OwnerId = "tester",
            Name = "测试视频模型",
            Provider = provider,
            Endpoint = endpoint,
            ModelKey = modelKey,
            DeliveryJson = deliveryJson,
        };

    private static VideoDeliveryRequest CreateRequest(
        VideoDeliverySpec spec,
        string? firstFrame = null,
        string? resolution = "1280x720",
        int? duration = 5,
        IReadOnlyDictionary<string, object?>? extra = null)
        => new(
            CreateModel(),
            "一只猫在草地上奔跑",
            null,
            firstFrame,
            resolution,
            duration,
            null,
            12345,
            extra ?? new Dictionary<string, object?>());

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void 百炼文生视频_请求体应写入_input结构()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;
        var body = VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec), spec);
        var root = Parse(body);

        // 百炼的提示词包在 input 里，而不是顶层
        Assert.Equal("一只猫在草地上奔跑", root["input"]!["prompt"]!.GetValue<string>());
        Assert.Equal("wan2.6-i2v-flash", root["model"]!.GetValue<string>());

        // 万相 2.6+ 用 resolution + ratio 分离表达分辨率
        Assert.Equal("720P", root["parameters"]!["resolution"]!.GetValue<string>());
        Assert.Equal("16:9", root["parameters"]!["ratio"]!.GetValue<string>());
        Assert.Equal(5, root["parameters"]!["duration"]!.GetValue<int>());

        // 文生视频不应出现首帧字段
        Assert.Null(root["input"]!["img_url"]);
    }

    [Fact]
    public void 百炼图生视频_首帧应写入_input_img_url()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;
        var dataUri = "data:image/png;base64,AAAA";
        var body = VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec, dataUri), spec);
        var root = Parse(body);

        // 百炼的首帧字段是 input.img_url，不是 image_url / first_frame
        Assert.Equal(dataUri, root["input"]!["img_url"]!.GetValue<string>());
        Assert.Null(root["input"]!["image_url"]);
    }

    [Fact]
    public void OpenAI_请求体应为顶层字段且分辨率用像素写法()
    {
        var spec = VideoDeliveryPresets.OpenAiVideos;
        var body = VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec), spec);
        var root = Parse(body);

        // OpenAI 的提示词在顶层，不套 input
        Assert.Equal("一只猫在草地上奔跑", root["prompt"]!.GetValue<string>());
        Assert.Equal("1280x720", root["size"]!.GetValue<string>());
        Assert.Equal(5, root["seconds"]!.GetValue<int>());

        // OpenAI 预设未配置负向提示词字段，不该凭空造出来
        Assert.Null(root["negative_prompt"]);
    }

    [Fact]
    public void 分辨率_像素转档位与宽高比应正确()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;

        // 竖屏 720x1280 → 720P + 9:16。
// 档位看的是<b>短边</b>：按高度判断会把竖屏 720P 误判成 1080P
var portrait = Parse(VideoRequestBuilder.BuildSubmitBody(
            CreateRequest(spec, resolution: "720x1280"), spec));
        Assert.Equal("720P", portrait["parameters"]!["resolution"]!.GetValue<string>());
        Assert.Equal("9:16", portrait["parameters"]!["ratio"]!.GetValue<string>());

        // 1080 高度 → 1080P
        var fullHd = Parse(VideoRequestBuilder.BuildSubmitBody(
            CreateRequest(spec, resolution: "1920x1080"), spec));
        Assert.Equal("1080P", fullHd["parameters"]!["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void 分辨率_用户已填档位时应原样使用()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;
        var body = Parse(VideoRequestBuilder.BuildSubmitBody(
            CreateRequest(spec, resolution: "1080P"), spec));

        // 用户直接填了 1080P，不能被再推导一次变成 720P
        Assert.Equal("1080P", body["parameters"]!["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void 扩展参数应挂到预设指定的容器下()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;
        var extra = new Dictionary<string, object?>
        {
            ["prompt_extend"] = true,
            ["watermark"] = false,
        };

        var root = Parse(VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec, extra: extra), spec));

        // 百炼要求扩展参数放进 parameters
        Assert.True(root["parameters"]!["prompt_extend"]!.GetValue<bool>());
        Assert.False(root["parameters"]!["watermark"]!.GetValue<bool>());
    }

    [Fact]
    public void 字段路径_应支持数组下标()
    {
        var root = Parse("""
        {
          "results": {
            "videos": [
              { "url": "https://cdn.example.com/a.mp4" },
              { "url": "https://cdn.example.com/b.mp4" }
            ]
          }
        }
        """);

        Assert.Equal(
            "https://cdn.example.com/a.mp4",
            JsonNodePath.GetScalar(root, "results.videos[0].url"));

        Assert.Equal(
            "https://cdn.example.com/b.mp4",
            JsonNodePath.GetScalar(root, "results.videos[1].url"));

        // 下标越界应返回 null 而不是抛异常
        Assert.Null(JsonNodePath.GetScalar(root, "results.videos[5].url"));
    }

    [Fact]
    public void 字段路径_嵌套写入应保留同级兄弟字段()
    {
        var root = new JsonObject();

        // 连续写两个 input.* 字段，父节点不能被覆盖
        JsonNodePath.Set(root, "input.prompt", JsonValue.Create("提示词"));
        JsonNodePath.Set(root, "input.negative_prompt", JsonValue.Create("负向"));

        Assert.Equal("提示词", root["input"]!["prompt"]!.GetValue<string>());
        Assert.Equal("负向", root["input"]!["negative_prompt"]!.GetValue<string>());
    }

    [Fact]
    public void 百炼响应_应能从_output结构读出任务号与视频地址()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;

        var submitResponse = Parse("""
        {
          "output": { "task_status": "PENDING", "task_id": "0385dc79-5ff8" },
          "request_id": "4909100c"
        }
        """);

        Assert.Equal("0385dc79-5ff8", JsonNodePath.GetScalar(submitResponse, spec.TaskIdPath));

        var doneResponse = Parse("""
        {
          "output": {
            "task_id": "0385dc79-5ff8",
            "task_status": "SUCCEEDED",
            "video_url": "https://dashscope-result.oss.mp4?Expires=1"
          }
        }
        """);

        Assert.Equal("SUCCEEDED", JsonNodePath.GetScalar(doneResponse, spec.StatusValuePath));
        Assert.Equal(
            "https://dashscope-result.oss.mp4?Expires=1",
            JsonNodePath.GetScalar(doneResponse, spec.VideoUrlPath));
    }

    [Fact]
    public void OpenAI_产物形态应为二进制下载而非地址()
    {
        var spec = VideoDeliveryPresets.OpenAiVideos;

        // OpenAI 的查询响应里没有任何视频地址，只有状态
        var response = Parse("""
        {
          "id": "video_123",
          "object": "video",
          "status": "completed",
          "progress": 100,
          "size": "1280x720",
          "seconds": "8"
        }
        """);

        Assert.Equal("video_123", JsonNodePath.GetScalar(response, spec.TaskIdPath));
        Assert.Equal("completed", JsonNodePath.GetScalar(response, spec.StatusValuePath));
        Assert.Null(JsonNodePath.GetScalar(response, spec.VideoUrlPath));

        // 必须走 content 接口拿正文
        Assert.Equal(VideoOutputKind.ContentBinary, spec.OutputKind);
        Assert.Equal(
            "https://api.openai.com/v1/videos/video_123/content",
            spec.ResolveContentUrl(CreateModel("https://api.openai.com/v1"), "video_123"));
    }

    [Fact]
    public void SiliconFlow_轮询应为POST且任务号放在body字段()
    {
        var spec = VideoDeliveryPresets.SiliconFlowVideo;
        var model = CreateModel("https://api.siliconflow.cn/v1", "Wan-AI/Wan2.2-T2V-A14B", provider: "硅基流动");

        // 任务号不能拼进 URL——该平台只接受 POST 固定地址 + body 传 id
        Assert.Equal("POST", spec.StatusMethod);
        Assert.Equal("requestId", spec.StatusIdField);
        Assert.Equal("https://api.siliconflow.cn/v1/video/status", spec.ResolveStatusUrl(model, "rid-1"));

        var response = Parse("""
        {
          "status": "Succeed",
          "results": { "videos": [ { "url": "https://cdn.siliconflow.cn/out.mp4" } ] }
        }
        """);

        Assert.Equal("Succeed", JsonNodePath.GetScalar(response, spec.StatusValuePath));
        Assert.Equal(
            "https://cdn.siliconflow.cn/out.mp4",
            JsonNodePath.GetScalar(response, spec.VideoUrlPath));
    }

    [Fact]
    public void 提交地址_不应产生双前缀()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;

        // endpoint 填到 /api/v1，拼上完整服务路径后仍是单层前缀
        var url = spec.ResolveSubmitUrl(CreateModel("https://dashscope.aliyuncs.com/api/v1"));

        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis",
            url);

        // 末尾斜杠不应产生 // 双斜杠
        var withSlash = spec.ResolveSubmitUrl(CreateModel("https://dashscope.aliyuncs.com/api/v1/"));

        Assert.DoesNotContain("//api", withSlash!.Replace("https://", string.Empty));
    }

    [Fact]
    public void 提交地址_应丢弃聊天用的OpenAI兼容层前缀()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;

        // 用户从聊天模型整段复制 endpoint：视频接口在百炼没有兼容层，
        // 直接相加会得到 /compatible-mode/v1/api/v1/... —— 服务端只回 404
        var url = spec.ResolveSubmitUrl(CreateModel("https://dashscope.aliyuncs.com/compatible-mode/v1"));

        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis",
            url);

        // 轮询地址同样要回到域名根，否则提交成功后一步也会 404
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/tasks/task-1",
            spec.ResolveStatusUrl(CreateModel("https://dashscope.aliyuncs.com/compatible-mode/v1"), "task-1"));

        // 业务空间专属域名同样适用
        Assert.Equal(
            "https://ws-123.cn-beijing.maas.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis",
            spec.ResolveSubmitUrl(CreateModel("https://ws-123.cn-beijing.maas.aliyuncs.com/compatible-mode/v1")));
    }

    [Fact]
    public void 提交地址_兼容层前缀不应影响OpenAI预设()
    {
        // OpenAI 预设本身就是兼容协议，endpoint 里的前缀是真实路径的一部分。
        // 若一并丢弃，自建网关（/proxy 这类）的地址会被拼坏。
        var spec = VideoDeliveryPresets.OpenAiVideos;

        Assert.Equal(
            "https://gateway.example.com/proxy/v1/videos",
            spec.ResolveSubmitUrl(CreateModel("https://gateway.example.com/proxy")));

        Assert.Equal(
            "https://api.openai.com/v1/videos",
            spec.ResolveSubmitUrl(CreateModel("https://api.openai.com/v1")));
    }

    [Fact]
    public void 预设推断_应按主机名与模型名命中()
    {
        // 百炼：主机名带 aliyuncs
        Assert.Equal(
            "dashscope-video",
            VideoDeliveryResolver.Resolve(CreateModel())!.PresetName);

        // 硅基流动：主机名带 siliconflow。
        // 它的模型名 Wan-AI/Wan2.2-T2V-A14B 含 "wan"，若按模型名优先判断会被误判成百炼，
        // 因此这里专门覆盖"必须靠主机名识别"这个回归点
        var siliconFlow = VideoDeliveryResolver.Resolve(
            CreateModel("https://api.siliconflow.cn/v1", "Wan-AI/Wan2.2-T2V-A14B"));

        Assert.Equal("siliconflow-video", siliconFlow!.PresetName);

        // 其它网关：兜底走 OpenAI 形状，这正是"只填 endpoint 就能用"的依据
        var fallback = VideoDeliveryResolver.Resolve(
            CreateModel("https://api.example.com/v1", "some-video-model", provider: "自建"));

        Assert.Equal("openai-videos", fallback!.PresetName);
    }

    [Fact]
    public void 预设推断_第三方网关上的万相应按代际选参数形态()
    {
        // maas.qianwenaiapi.com 这类网关主机名既不含 dashscope 也不含 aliyuncs，
        // 只能靠模型名识别；而 wan3.0 走的是新版「resolution + ratio」形态，
        // 若按旧的 wan → 旧预设处理，会拿 2.5 的 parameters.size 去调 3.0。
        var wan3 = VideoDeliveryResolver.Resolve(
            CreateModel("https://maas.qianwenaiapi.com", "wan3.0-video"));

        Assert.Equal("dashscope-video", wan3!.PresetName);

        // 同一网关上的老模型（非 2.2，避开硅基流动那条模型名规则）仍应走旧形态
        var wan25 = VideoDeliveryResolver.Resolve(
            CreateModel("https://maas.qianwenaiapi.com", "wan2.5-i2v"));

        Assert.Equal("dashscope-legacy-video", wan25!.PresetName);

        // 已知歧义：wan2.2 在"认不出主机名"时按硅基流动处理，
        // 因为该平台的万相模型就叫 Wan-AI/Wan2.2-T2V-A14B。
        // 第三方网关上若判断错了，在交付预设里显式选一个即可覆盖。
        var wan22 = VideoDeliveryResolver.Resolve(
            CreateModel("https://maas.qianwenaiapi.com", "wan2.2-i2v-plus"));

        Assert.Equal("siliconflow-video", wan22!.PresetName);
    }

    [Fact]
    public void 档位分辨率_应搭配adaptive而不是写死16比9()
    {
        var spec = VideoDeliveryPresets.DashScopeVideo;

        // 官方示例：只给清晰度，比例交给模型自适应
        var body = VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec, resolution: "480P"), spec);
        var root = Parse(body);

        Assert.Equal("480P", JsonNodePath.GetScalar(root, "parameters.resolution"));
        Assert.Equal("adaptive", JsonNodePath.GetScalar(root, "parameters.ratio"));

        // 给了像素尺寸才推导具体比例
        var sized = Parse(VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec, resolution: "1280x720"), spec));

        Assert.Equal("720P", JsonNodePath.GetScalar(sized, "parameters.resolution"));
        Assert.Equal("16:9", JsonNodePath.GetScalar(sized, "parameters.ratio"));
    }

    [Fact]
    public void 用户模板_应能覆盖预设且未写的字段沿用预设()
    {
        // 某厂商提交路径与百炼不同，但其余字段结构一致 —— 只覆盖路径即可
        var vendorModel = CreateModel(
            "https://vendor.example.com/api/v1",
            "wan2.6-i2v",
            VideoDeliveryResolver.SerializeTemplate(new VideoDeliveryTemplate
            {
                Preset = "dashscope-video",
                SubmitPath = "/api/v1/custom/video-generation",
            }));

        var spec = VideoDeliveryResolver.Resolve(vendorModel);

        Assert.Equal("dashscope-video", spec!.PresetName);

        // 必须用同一个模型解析地址：地址取决于 endpoint，
        // 若换成默认模型的 endpoint 就测不到"覆盖路径后拼接是否正确"
        Assert.Equal(
            "https://vendor.example.com/api/v1/custom/video-generation",
            spec.ResolveSubmitUrl(vendorModel));

        // 未覆盖的字段必须保持预设值
        Assert.Equal("output.task_id", spec.TaskIdPath);
        Assert.Equal("input.img_url", spec.RequestFields.FirstFrame);
    }

    [Fact]
    public void 用户模板_空串应表示该字段不传()
    {
        // 用户想显式关掉负向提示词：空串 = 不传，而不是"用预设默认值"
        var template = new VideoDeliveryTemplate
        {
            Preset = "dashscope-video",
            RequestFields = new VideoRequestFieldTemplate { NegativePrompt = string.Empty },
        };

        var spec = VideoDeliveryResolver.Resolve(CreateModel(
            deliveryJson: VideoDeliveryResolver.SerializeTemplate(template)));

        Assert.Null(spec!.RequestFields.NegativePrompt);

        var body = Parse(VideoRequestBuilder.BuildSubmitBody(CreateRequest(spec), spec));
        Assert.Null(body["input"]!["negative_prompt"]);
    }

    [Fact]
    public void 用户模板_可覆盖产物形态与状态值()
    {
        var template = new VideoDeliveryTemplate
        {
            Preset = "openai-videos",
            OutputKind = "url",
            VideoUrlPath = "data.video_url",
            SucceededStatus = "SUCCESS",
            StatusValuePath = "state",
        };

        var spec = VideoDeliveryResolver.Resolve(CreateModel(
            deliveryJson: VideoDeliveryResolver.SerializeTemplate(template)));

        Assert.Equal(VideoOutputKind.Url, spec!.OutputKind);
        Assert.Equal("data.video_url", spec.VideoUrlPath);
        Assert.Equal("SUCCESS", spec.SucceededStatus);
        Assert.Equal("state", spec.StatusValuePath);
    }

    [Fact]
    public void 模板JSON格式错误时不应抛异常()
    {
        // 坏 JSON 应当降级为"用预设推断"，而不是让整个模型配置读取失败
        var spec = VideoDeliveryResolver.Resolve(CreateModel(deliveryJson: "{ 这不是合法 JSON"));

        Assert.NotNull(spec);
        Assert.Equal("dashscope-video", spec!.PresetName);
    }
}