using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WooVisAgent.EntityFrameworkCore.Diagnostics;

/// <summary>
/// 单实例检测：同一套数据库上只允许跑一套 Api / GenerateWorker。
///
/// <para><b>为什么需要它</b>：误启动第二套实例时，症状极其难懂——
/// 服务照常启动、接口照常返回，唯独<b>进度推送会随机丢失</b>。
/// 因为状态变更消息走 RabbitMQ 的 <c>WooVisAgent.Api</c> 队列，
/// 两个 Api 进程会轮流消费，浏览器连着 A 时，那条"生成完成"很可能被 B 消费走。
/// 用户看到的就是进度条停在 92% 不动，而任务其实早就成功了。
/// 同样地，第二个 AppHost 抢不到 5199，Aspire 注入的前端代理地址会指向另一个进程，
/// 表现为页面能打开但所有接口 502。</para>
///
/// <para>这里用 PostgreSQL 的会话级 advisory lock 做标记：拿到锁的进程活着就一直占着，
/// 进程退出连接关闭后自动释放，不需要额外的清理逻辑。</para>
///
/// <para><b>只告警、不阻断</b>：即使检测不到锁，服务照常运行，
/// 避免因为数据库临时不可用把整个系统卡在起不来。</para>
/// </summary>
public sealed class SingleInstanceGuard(
    IServiceScopeFactory scopeFactory,
    ILogger<SingleInstanceGuard> logger) : BackgroundService
{
    /// <summary>任意常量即可，只要全项目一致且不与其它用途冲突</summary>
    private const long AdvisoryLockKey = 0x57564D49; // "WVMI"

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IServiceScope? scope = null;

        try
        {
            scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AiCopilotDbContext>();

            // 内存库（集成测试）没有连接，也没有"多实例"概念
            if (!dbContext.Database.IsRelational())
                return;

            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync(stoppingToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.Value = AdvisoryLockKey;
            command.Parameters.Add(parameter);

            var acquired = Convert.ToBoolean(await command.ExecuteScalarAsync(stoppingToken));

            if (acquired)
            {
                logger.LogInformation("单实例检测通过：没有其他实例在占用本数据库。");
            }
            else
            {
                logger.LogError(
                    "检测到同一数据库上已经有一个实例在运行。多半是重复启动了 AppHost，后果是：" +
                    "① 生成进度推送被两个 Api 进程轮流消费，页面进度会停在某个数字不动；" +
                    "② 新实例抢不到 5199，Aspire 注入的前端代理可能指向已退出的进程，接口全部 502。" +
                    "请先停掉旧的那套（任务管理器里结束 WooVisAgent.AppHost / Api / GenerateWorker），再启动一次。");
            }

            // 连接保持打开，锁随进程退出自动释放
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            // 检测本身失败不影响业务
            logger.LogWarning(ex, "单实例检测未能执行，不影响服务启动");
        }
        finally
        {
            scope?.Dispose();
        }
    }
}