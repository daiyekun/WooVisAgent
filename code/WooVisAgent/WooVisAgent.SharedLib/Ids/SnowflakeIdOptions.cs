namespace WooVisAgent.SharedLib.Ids;

/// <summary>
/// 雪花算法配置项，对应 appsettings 的 Snowflake 节。
/// </summary>
public class SnowflakeIdOptions
{
    public const string SectionName = "Snowflake";

    /// <summary>数据中心标识，0 - 31</summary>
    public int DatacenterId { get; set; }

    /// <summary>机器（工作节点）标识，0 - 31</summary>
    public int WorkerId { get; set; }

    /// <summary>起始时间戳（毫秒）默认值：2024-01-01T00:00:00Z，决定了 41 位时间戳可用的年限</summary>
    public const long DefaultEpochMilliseconds = 1704067200000L;

    /// <summary>起始时间戳（毫秒）</summary>
    public long EpochMilliseconds { get; set; } = DefaultEpochMilliseconds;
}
