using System.Text.Json.Serialization;
using WooVisAgent.SharedLib.Delivery;

namespace WooVisAgentServices.ModelService.Dtos;

/// <summary>下拉类型参数的候选项</summary>
public sealed class ModelParamOptionModel
{
    public string Label { get; set; } = string.Empty;

    public object? Value { get; set; }
}

/// <summary>
/// 模型参数规格。字段与前端 src/types/index.ts 的 ModelParamSpec 对齐，
/// 落库时整体序列化成 JSON 存进 ModelConfig.ParamsJson。
/// </summary>
public sealed class ModelParamSpecModel
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>number / string / boolean / select</summary>
    public string Type { get; set; } = "string";

    public object? DefaultValue { get; set; }

    public List<ModelParamOptionModel>? Options { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    public double? Step { get; set; }

    public bool? Required { get; set; }

    public string? Placeholder { get; set; }

    public string? Description { get; set; }
}

/// <summary>新增 / 编辑模型配置的入参，字段与前端 SaveModelInput 对齐</summary>
public sealed class ModelConfigInput
{
    /// <summary>编辑时回传，新增时为空（主键由服务端生成）</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    /// <summary>image / video / llm</summary>
    public string Category { get; set; } = "image";

    public string? ModelKey { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>留空表示沿用已保存的密钥</summary>
    public string? ApiKey { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsDefault { get; set; }

    public List<ModelParamSpecModel>? Params { get; set; }

    /// <summary>
    /// 视频交付模板（仅视频模型需要）。
    ///
    /// 留空即可按预设自动推断；只有当该厂商的接口形状与预设不一致时
    /// 才需要覆盖个别字段（如提交路径、任务号字段、产物类型）。
    /// </summary>
    public VideoDeliveryTemplate? Delivery { get; set; }

    /// <summary>是否支持文生视频。默认 true；只支持图生视频的模型应关掉它。</summary>
    public bool SupportsTextToVideo { get; set; } = true;

    /// <summary>是否支持图生视频。默认 true；只支持文生视频的模型应关掉它。</summary>
    public bool SupportsImageToVideo { get; set; } = true;

    public string? Remark { get; set; }
}

/// <summary>
/// 模型配置出参。主键以字符串输出，避免前端 JS 丢失 64 位精度。
/// ApiKey 只在“查询本人单条配置”时赋值；其它接口保持 null，并通过 JsonIgnore 不输出该字段。
/// </summary>
public sealed record ModelConfigDto(
    string Id,
    string Name,
    string Provider,
    string Category,
    string? ModelKey,
    string? Endpoint,
    bool Enabled,
    bool IsDefault,
    IReadOnlyList<ModelParamSpecModel> Params,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    VideoDeliveryTemplate? Delivery = null,
    bool SupportsTextToVideo = true,
    bool SupportsImageToVideo = true,
    /// <summary>
    /// <b>解析后</b>的视频能力声明（预设 + 用户模板合并的结果）。
    ///
    /// 单独出参而不是让前端去猜：能力写在预设里，用户只要选了「火山方舟 Seedance」
    /// 预设就该看到三种模式，而模板 JSON 里可能根本没有 capabilities 这一段。
    /// 前端靠它决定模式下拉里哪些项可选。
    /// </summary>
    VideoCapabilitySpec? VideoCapabilities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApiKey = null);

/// <summary>连通性测试结果</summary>
public sealed record ModelTestResultDto(
    bool Success,
    int? Latency,
    string? Message,
    string? ResolvedUrl = null);

/// <summary>供应方支持的模型清单项</summary>
public sealed record ProviderModelDto(string Key, string Name);
