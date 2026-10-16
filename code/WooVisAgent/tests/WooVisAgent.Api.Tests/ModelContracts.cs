namespace WooVisAgent.Api.Tests;

/// <summary>模型配置接口路径</summary>
public static class ModelEndpoints
{
    public const string Base = "/api/models";

    public static string Detail(string id) => $"{Base}/{id}";

    public static string Enabled(string id) => $"{Base}/{id}/enabled";

    public static string Default(string id) => $"{Base}/{id}/default";

    public static string Test(string id) => $"{Base}/{id}/test";

    public const string TestDraft = $"{Base}/test";

    public static string ProviderModels(string provider) => $"{Base}/providers/{provider}/models";
}

/// <summary>新增 / 编辑模型配置的请求体，字段与前端 SaveModelInput 对齐</summary>
public record SaveModelRequest(
    string Name,
    string Provider,
    string Category,
    string? ModelKey = null,
    string? Endpoint = null,
    string? ApiKey = null,
    bool Enabled = true,
    bool IsDefault = false,
    List<ModelParamSpecPayload>? Params = null,
    string? Remark = null);

public record ModelParamSpecPayload(
    string Key,
    string Label,
    string Type,
    object? DefaultValue = null,
    List<ModelParamOptionPayload>? Options = null);

public record ModelParamOptionPayload(string Label, object Value);

/// <summary>模型配置出参。Id 固定为字符串（雪花 ID 超出 JS 安全整数范围）</summary>
public record ModelResult(
    string Id,
    string Name,
    string Provider,
    string Category,
    string? ModelKey,
    string? Endpoint,
    bool Enabled,
    bool IsDefault,
    List<ModelParamSpecPayload> Params,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record ModelTestPayload(bool Success, int? Latency, string? Message);

public record ToggleEnabledPayload(bool Enabled);
