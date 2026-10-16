namespace WooVisAgent.SharedLib.Ids;

/// <summary>
/// 64 位分布式主键生成器。数据库主键统一使用 long，由本接口产出雪花 ID。
/// </summary>
public interface ISnowflakeIdGenerator
{
    /// <summary>生成下一个 64 位主键（单调递增、全局唯一、恒为正数）</summary>
    long NextId();
}
