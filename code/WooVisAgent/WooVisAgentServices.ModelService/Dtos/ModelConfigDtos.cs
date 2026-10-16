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

    public string? Remark { get; set; }
}

/// <summary>模型配置出参。主键以字符串输出，避免前端 JS 丢失 64 位精度；密钥不回传。</summary>
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
    DateTimeOffset UpdatedAt);

/// <summary>连通性测试结果</summary>
public sealed record ModelTestResultDto(
    bool Success,
    int? Latency,
    string? Message,
    string? ResolvedUrl = null);

/// <summary>供应方支持的模型清单项</summary>
public sealed record ProviderModelDto(string Key, string Name);
