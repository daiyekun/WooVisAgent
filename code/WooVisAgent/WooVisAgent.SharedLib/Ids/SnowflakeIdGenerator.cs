namespace WooVisAgent.SharedLib.Ids;

/// <summary>
/// 标准 Twitter Snowflake 实现（64 位，恒为正数）。
///
/// 位布局：1 位符号位（恒 0）| 41 位毫秒时间戳 | 5 位数据中心 | 5 位工作节点 | 12 位序列号
/// 容量：单节点每毫秒 4096 个 ID（约 409.6 万/秒），时间戳可用 41 年。
/// </summary>
public sealed class SnowflakeIdGenerator : ISnowflakeIdGenerator
{
    private const int WorkerIdBits = 5;
    private const int DatacenterIdBits = 5;
    private const int SequenceBits = 12;

    private const long MaxWorkerId = (1L << WorkerIdBits) - 1;      // 31
    private const long MaxDatacenterId = (1L << DatacenterIdBits) - 1; // 31
    private const long MaxSequence = (1L << SequenceBits) - 1;      // 4095

    private const int WorkerIdShift = SequenceBits;                       // 12
    private const int DatacenterIdShift = SequenceBits + WorkerIdBits;    // 17
    private const int TimestampShift = SequenceBits + WorkerIdBits + DatacenterIdBits; // 22

    private readonly long _datacenterId;
    private readonly long _workerId;
    private readonly long _epochMilliseconds;
    private readonly Lock _gate = new();

    private long _sequence;
    private long _lastTimestamp = -1L;

    public SnowflakeIdGenerator(SnowflakeIdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WorkerId < 0 || options.WorkerId > MaxWorkerId)
            throw new ArgumentOutOfRangeException(nameof(options), $"WorkerId 必须在 0 - {MaxWorkerId} 之间。");
        if (options.DatacenterId < 0 || options.DatacenterId > MaxDatacenterId)
            throw new ArgumentOutOfRangeException(nameof(options), $"DatacenterId 必须在 0 - {MaxDatacenterId} 之间。");

        _workerId = options.WorkerId;
        _datacenterId = options.DatacenterId;
        _epochMilliseconds = options.EpochMilliseconds > 0
            ? options.EpochMilliseconds
            : SnowflakeIdOptions.DefaultEpochMilliseconds;
    }

    public long NextId()
    {
        lock (_gate)
        {
            var timestamp = CurrentMilliseconds();

            // 时钟回拨：等待追上上次的时间戳，避免生成重复 ID
            if (timestamp < _lastTimestamp)
            {
                SpinWaitUntil(_lastTimestamp);

                if (timestamp < _lastTimestamp)
                    throw new InvalidOperationException(
                        $"检测到系统时钟回拨（{_lastTimestamp - timestamp} ms），已拒绝生成 ID。");
            }

            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & MaxSequence;

                // 当前毫秒的序列号用尽，等待下一毫秒
                if (_sequence == 0)
                    timestamp = SpinWaitUntil(_lastTimestamp);
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = timestamp;

            return ((timestamp - _epochMilliseconds) << TimestampShift)
                   | (_datacenterId << DatacenterIdShift)
                   | (_workerId << WorkerIdShift)
                   | _sequence;
        }
    }

    private static long CurrentMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>自旋等待，直到时间戳大于 <paramref name="lastTimestamp" />；超过 1 秒仍无进展则放弃等待</summary>
    private static long SpinWaitUntil(long lastTimestamp)
    {
        var deadline = Environment.TickCount64 + 1000;
        long current = lastTimestamp;

        while (current <= lastTimestamp && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
            current = CurrentMilliseconds();
        }

        return current;
    }
}
