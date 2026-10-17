namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 用户自行接入的模型配置。
///
/// 主键为 64 位雪花 ID（见 <see cref="Ids.ISnowflakeIdGenerator" />），
/// 配置按用户隔离（<see cref="OwnerId" /> 对应用户的 IdentityUser.Id）。
/// </summary>
public class ModelConfig : BaseEntity<long>, IAggregateRoot, IAggregateRoot<long>
{
    /// <summary>展示名称，如「即梦 3.0 文生视频」</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>配置归属用户（IdentityUser.Id），实现用户间数据隔离</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>模型分类</summary>
    public ModelCategory Category { get; set; } = ModelCategory.Image;

    /// <summary>供应方，如 OpenAI / 阿里云百炼 / 本地 ComfyUI</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>调用后端接口时使用的模型标识</summary>
    public string? ModelKey { get; set; }

    /// <summary>接口地址</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// 访问密钥。仅在服务端存储，接口出参一律脱敏（不回传明文）。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>是否启用；停用后不再出现在新建任务的模型选择里</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否为该分类下的默认模型（同用户同分类内互斥）</summary>
    public bool IsDefault { get; set; }

    /// <summary>参数规格的 JSON 序列化结果，用于渲染任务提交表单</summary>
    public string ParamsJson { get; set; } = "[]";

    /// <summary>
    /// 视频交付模板的 JSON 序列化结果，见 <c>SharedLib.Delivery.VideoDeliveryTemplate</c>。
    ///
    /// 只有视频类模型会用到。留空（默认 <c>{}</c>）时按 <see cref="VideoDeliveryPreset" /> 推断，
    /// 因此「OpenAI 兼容网关」这类常见场景不填也能直接跑；只有当预设推断出的形状不对时
    /// 才需要显式覆盖某个字段。
    /// </summary>
    public string DeliveryJson { get; set; } = "{}";

    /// <summary>
    /// 是否支持「文生视频」（只给提示词，不给首帧图）。
    ///
    /// 很多视频模型只支持其中一种（如 <c>wanx2.1-i2v-turbo</c> 只能图生视频）。
    /// 声明后，前端会自动隐藏不适用的输入框、后端会在提交前拦下来，
    /// 避免出现「传了首帧图但模型悄悄忽略」这种用户完全无法察觉的错位。
    /// 默认 <c>true</c>：绝大多数模型两种都支持，历史数据也按此处理。
    /// </summary>
    public bool SupportsTextToVideo { get; set; } = true;

    /// <summary>是否支持「图生视频」（提供首帧图作为起始画面）</summary>
    public bool SupportsImageToVideo { get; set; } = true;

    /// <summary>备注</summary>
    public string? Remark { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
