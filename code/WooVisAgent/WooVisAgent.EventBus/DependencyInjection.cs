using JasperFx.CodeGeneration.Model;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using WooVisAgent.EventBus.Events;

namespace WooVisAgent.EventBus;

/// <summary>
/// 事件总线注册。
///
/// 传输层用的是 <b>Wolverine（MIT 协议）+ RabbitMQ</b>。
/// 之前用的是 MassTransit 9，而 MassTransit 从 v9 起改成商业授权：
/// 没有许可证时进程会在启动阶段直接抛 “License must be specified …”，
/// Api 与 Worker 都起不来，页面上表现为任务一直排队 / 转圈 ——
/// 很容易被误判成业务逻辑的 bug。Wolverine 是 MIT 协议，无需任何许可证即可运行。
/// </summary>
public static class DependencyInjection
{
    public static void AddEventBus(
        this IHostApplicationBuilder builder,
        string listenQueue,
        params Assembly[] assemblies)
    {
        // 从 Aspire 注入的连接字符串中读取配置
        // 连接字符串名必须与 AppHost 中 .AddRabbitMQ("eventbus") 的名称一致
        var connectionString = builder.Configuration.GetConnectionString("eventbus");

        // 显式关闭开关：集成测试用它把整个消息总线摘掉。
        //
        // 为什么不靠「InMemory 数据库就换传输层」自动判断：
        //   1) Wolverine 在宿主<b>启动阶段</b>就会去连 broker，连不上就一直重连，
        //      测试会整体卡死（本次踩过：84 个用例只跑出 3 个，耗时 4 分 46 秒）；
        //   2) 自动判断意味着「测试跑一套、上线跑另一套」，
        //      迟早出现「测试全绿、上线连不上队列」这种最难查的问题。
        //
        // 开关只用于测试，生产环境不设置，走的永远是 RabbitMQ 那一条路径。
        if (builder.Configuration.GetValue("EventBus:Enabled", true) is false)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // 不给 5672 这类"看起来能用"的兜底值。
            // 之前写死 localhost:5672，而本机该端口常被别的 RabbitMQ 容器占着，
            // 于是注入失败时会静默连到别人的队列：消息发出去没报错，
            // 任务却永远停在 queued，且日志里看不出任何异常。
            // 拿不到连接串就直接抛错，让问题在启动时暴露。
            throw new InvalidOperationException(
                "缺少 ConnectionStrings:eventbus。请通过 AppHost 启动（WithReference 注入），"
                + "或显式配置该连接串。");
        }

        builder.UseWolverine(opts =>
        {
            // Wolverine 6 起，处理器代码默认在运行时用 Roslyn 动态编译，
            // 而 Roslyn 编译器被拆到 WolverineFx.RuntimeCompilation 包里。
            // 不显式开启就直接抛 "no IAssemblyGenerator registered"，
            // 而且这个异常发生在 Host.StartAsync，表现为进程启动后立刻退出。
            // （生产环境想省掉这段编译开销，可以改用 `dotnet run -- codegen write` 预生成。）
            opts.UseRuntimeCompilation();

// 关键：Wolverine 6 起 ServiceLocationPolicy 默认是 NotAllowed，
      // 处理器的构造函数里只要有服务依赖，就会在第一次收到消息时抛
      // InvalidServiceLocationException，并把消息丢进死信队列 ——
      // 表现是任务永远停在 queued，而 Api 侧完全看不到任何错误。
      //
      // 本项目的两个处理器都从构造函数注入（仓储、生成服务、SignalR Hub），
      // 因此必须显式放开服务定位。
      opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

            opts.UseRabbitMq(new Uri(connectionString))
                // 启动时自动声明队列与绑定，免得手工去 RabbitMQ 控制台建
                .AutoProvision();

            // 本进程消费的队列。Api 与 Worker 各听一条，互不抢消息
            opts.ListenToRabbitQueue(listenQueue);

            // 显式路由：消息类型 → 目标队列。
            // 不用"约定路由"是因为队列名是团队约定好的（EventBusConstants），
            // 让框架按类型名推导会在改名时悄悄把队列换掉，出问题时很难查。
            opts.PublishMessage<ImageGenerationRequested>()
                .ToRabbitQueue(EventBusConstants.GenerateWorkerQueue);

            opts.PublishMessage<VideoGenerationRequested>()
                .ToRabbitQueue(EventBusConstants.GenerateWorkerQueue);

            opts.PublishMessage<GenerationTaskStatusChanged>()
                .ToRabbitQueue(EventBusConstants.ApiQueue);

            // 处理器发现：默认只扫入口程序集，这里把宿主指定的程序集补进来
            foreach (var assembly in assemblies)
                opts.Discovery.IncludeAssembly(assembly);
        });
    }
}
