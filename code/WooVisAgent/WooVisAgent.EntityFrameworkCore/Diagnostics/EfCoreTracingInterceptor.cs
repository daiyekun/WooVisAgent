using System.Data.Common;
using System.Diagnostics;

using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using WooVisAgent.SharedLib.Diagnostics;

namespace WooVisAgent.EntityFrameworkCore.Diagnostics;

/// <summary>
/// 把 EF Core 的每条 SQL 变成一个 span。
///
/// <para><b>为什么不引官方埋点包</b>：<c>OpenTelemetry.Instrumentation.EntityFrameworkCore</c>
/// 至今只有 beta 版本（nuget 上最新是 1.20.0-beta.1，没有稳定版），
/// 为了一条 SQL 跨度把预发布依赖引进整个解决方案并不划算。自己埋反而更可控：
/// 想要的属性一个不多，也没有 beta 包升级时的破坏性变更风险。</para>
///
/// <para><b>实现方式</b>：拦截命令执行前后各一次，用 <c>AsyncLocal</c> 暂存
/// 正在执行的 span，因为 EF 的拦截器回调拿不到 <c>Activity.Current</c>——
/// DbCommand 执行发生在异步流转中，靠 Activity.Current 关联并不可靠。</para>
/// </summary>
public sealed class EfCoreTracingInterceptor(IConfiguration? configuration = null) : DbCommandInterceptor
{
    private const string DbSystem = "postgresql";

    /// <summary>
    /// 当前 DbCommand 对应的 span。EF 可能在同一异步流里交错执行多条命令，
    /// 用 <c>AsyncLocal</c> 而不是普通字段，保证并发请求之间不会串。
    /// </summary>
    private static readonly AsyncLocal<Activity?> CurrentActivity = new();

    /// <summary>
    /// 是否把 SQL 文本写进 span。默认关闭——每条查询都带 SQL 会显著放大
    /// span 体积与内存占用，而排查偶发慢 SQL 时临时打开更实际。
    /// 需要时设环境变量 <c>OTEL_RECORD_SQL=true</c>。
    /// </summary>
    private readonly bool _recordSqlText = string.Equals(
        configuration?["OTEL_RECORD_SQL"],
        "true",
        StringComparison.OrdinalIgnoreCase);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        StartActivity(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        CompleteActivity();
        return base.ReaderExecuted(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        StartActivity(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override object ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object result)
    {
        CompleteActivity();
        return base.ScalarExecuted(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        StartActivity(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        CompleteActivity();
        return base.NonQueryExecuted(command, eventData, result);
    }

    /// <summary>命令失败时把异常记进 span。不吞异常，EF 的重试与错误上抛逻辑不受影响。</summary>
    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData)
    {
        var activity = CurrentActivity.Value;
        if (activity is not null)
        {
            activity.RecordFailure(eventData.Exception);
        }

        base.CommandFailed(command, eventData);
    }

    private void StartActivity(DbCommand command)
    {
        var operation = command.CommandText.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToUpperInvariant() ?? "QUERY";

        var activity = TraceSources.ModelSource.StartActivity($"db {operation}", ActivityKind.Client);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("db.system", DbSystem);
        activity.SetTag("db.operation", operation);
        activity.SetTag("wv.table", GuessTable(command.CommandText));

        if (_recordSqlText)
        {
            activity.SetTag("db.statement", command.CommandText);
        }

        CurrentActivity.Value = activity;
    }

    private static void CompleteActivity()
    {
        // Dispose 后状态自动落为 Ok；显式标一次是为了让面板上「成功 / 失败」一眼可分。
        CurrentActivity.Value?.RecordSuccess();
        CurrentActivity.Value?.Dispose();
        CurrentActivity.Value = null;
    }

    /// <summary>
    /// 从 SQL 里粗略猜出目标表，让面板上按表名就能筛。
    /// 只做正则匹配不做真正的 SQL 解析——够用且零依赖，
    /// 猜错也不影响正确性（db.statement 里的真实 SQL 仍然完整保留）。
    /// </summary>
    private static string? GuessTable(string sql)
    {
        // 双引号是 PostgreSQL 的标识符引用符（EF 生成 SQL 时会加），
        // 所以模式里必须同时容纳 " 和 两种情况，否则带引号的表名一个都匹配不上。
        const string TablePattern = "\\b(?:from|into|update)\\s+\"?([A-Za-z_][A-Za-z0-9_]*)";

        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            TablePattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }
}
