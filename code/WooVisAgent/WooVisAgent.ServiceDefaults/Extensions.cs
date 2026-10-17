using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WooVisAgent.ServiceDefaults;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // 开启标准弹性管道（重试 / 熔断 / 超时）。
            //
            // 必须显式放长超时：模板默认值是「单次尝试 10 秒、总超时 30 秒」，
            // 而本应用的出站调用主要是模型调用——图片生成动辄几十秒甚至几分钟。
            // 沿用默认值会让请求在真正返回前就被 Polly 掐断，任务列表里出现
            // “The operation didn't complete within the allowed timeout of '00:00:30'.”
            http.AddStandardResilienceHandler(HttpResilienceDefaults.Apply);

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var serviceName = ResolveServiceName(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            // 解析日志状态里的结构化键值。配合下面的 ResourceBuilder，
            // 日志条目会带上 TraceId / SpanId，面板上能从一条日志直接跳到产生它的链路。
            logging.ParseStateValues = true;
        });

        builder.Services.AddOpenTelemetry()
            // 资源属性。service.name 是链路在面板里归属到某个服务的唯一依据，
            // 缺了它所有 span 会混成一坨，看不出谁调用谁。
            .ConfigureResource(resource =>
            {
                resource.AddService(
                    serviceName: serviceName,
                    serviceVersion: typeof(Extensions).Assembly.GetName().Version?.ToString(),
                    serviceInstanceId: Environment.MachineName);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Microsoft.AspNetCore.Hosting")
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                    .AddMeter("System.Net.Http")
                    .AddMeter("System.Net.NameResolution")
                    // 把指标样本挂到对应 trace 上：点开一条链路能直接跳到该接口的 P99，
                    // 不用再切到指标页手动查。
                    .SetExemplarFilter(ExemplarFilterType.TraceBased);
            })
            .WithTracing(tracing =>
            {
                // 自定义 span 的订阅列表。ActivitySource 名字必须与
                // TraceSources 里的常量完全一致——AddSource 不匹配时不会报任何错，
                // 只是 span 静默消失，是最难查的一类问题。
                tracing.AddSource(WellKnownSources.Api)
                    .AddSource(WellKnownSources.Worker)
                    .AddSource(WellKnownSources.Model)
                    // 兼容按程序集名订阅的写法
                    .AddSource(builder.Environment.ApplicationName)

                    .AddAspNetCoreInstrumentation(tracing =>
                    {
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);

                        // 默认只对未处理异常建事件。登录密码错误、参数校验失败这些
                        // "正常流程里的失败"才是排查时最常看的，不记就只剩一句耗时。
                        tracing.RecordException = true;

                        tracing.EnrichWithHttpResponse = (activity, response) =>
                        {
                            // 状态码提到 span 顶层属性，面板上不用点开详情就能筛 5xx。
                            activity.SetTag("http.response.status_code", response.StatusCode);
                        };
                    })
                    .AddHttpClientInstrumentation(tracing =>
                    {
                        // 出站调用同样记异常：模型接口返回 400 / 配额耗尽是排查的头号线索。
                        // 不记的话只能看到「耗时 3000ms」，完全不知道服务端说了什么。
                        tracing.RecordException = true;

                        tracing.EnrichWithHttpResponseMessage = (activity, response) =>
                        {
                            activity.SetTag("http.response.status_code", (int)response.StatusCode);
                        };
                    });
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// ActivitySource 名字。必须与 <c>TraceSources</c> 里的常量逐字一致。
    /// </summary>
    private static class WellKnownSources
    {
        public const string Api = "WooVisAgent.Api";
        public const string Worker = "WooVisAgent.GenerateWorker";
        public const string Model = "WooVisAgent.SharedLib";
    }

    /// <summary>
    /// 服务名。优先用 Aspire 注入的 <c>OTEL_SERVICE_NAME</c>（等于 AppHost 里的资源名），
    /// 这样面板上的服务名和左侧资源列表对得上；脱离 AppHost 单跑时退回程序集名。
    /// </summary>
    private static string ResolveServiceName(IHostApplicationBuilder builder)
    {
        var fromAspire = builder.Configuration["OTEL_SERVICE_NAME"];
        if (!string.IsNullOrWhiteSpace(fromAspire))
        {
            return fromAspire;
        }

        return string.IsNullOrWhiteSpace(builder.Environment.ApplicationName)
            ? "WooVisAgent"
            : builder.Environment.ApplicationName;
    }

    /// <summary>
    /// 是否把 SQL 文本写进 span。默认关闭（体积与内存代价大），
    /// 需要时设环境变量 <c>OTEL_RECORD_SQL=true</c>。
    /// </summary>
    public static bool ShouldRecordSqlText(IConfiguration configuration)
    {
        return string.Equals(
            configuration["OTEL_RECORD_SQL"],
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var endpoint = ResolveOtlpEndpoint(builder.Configuration);

        // 启动时把导出目标打进日志。
        // 「面板里没有链路」是个沉默的故障——没有任何报错，只是没有数据。
        // 有了这行日志，一眼就能看出是端点没解析到，还是解析到了但数据没出去。
        //
        // 用 Console 而非 ILogger：这段代码在 builder 阶段执行，此时 LoggerFactory
        // 还没建好，拿不到 ILogger；而且这行信息必须在任何 DI 解析失败之前打出来。
        Console.WriteLine(
            endpoint is null
                ? $"[OpenTelemetry] 未找到 OTLP 端点，span 将输出到控制台（服务名：{ResolveServiceName(builder)}）"
                : $"[OpenTelemetry] 导出目标 {endpoint}（协议 gRPC，服务名：{ResolveServiceName(builder)}）");

        if (endpoint is not null)
        {
            // 显式指定端点与协议，而不是依赖 UseOtlpExporter() 自己去读环境变量。
            //
            // 原因：Aspire 注入的是自定义变量名（ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL），
            // 而 .NET SDK 默认只认 OTEL_EXPORTER_OTLP_ENDPOINT。用AppHost 启动时
            // 前者有值、后者没有，于是exporter 分支「看起来配置了」实际拿不到地址，
            // span 全程被静默丢弃——这正是「配了 OpenTelemetry 却看不到任何链路」的成因。
            //
            // 协议必须与 Aspire 面板实际暴露的一致。
            //
            // Aspire 13.x 的面板<b>只监听 OTLP/gRPC</b>（启动横幅里只有 "OTLP/gRPC: https://localhost:xxxxx"，
            // 没有 HTTP 那一行）。这里若按 HttpProtobuf 发送，面板收不到，
            // 日志里表现为反复出现 "HTTP/2 over TLS was not negotiated on an HTTP/2-only endpoint"。
            // 用 gRPC 才能真正把 span 送进面板的「追踪」页签。
            //
            // ============================ 重要前提 ============================
            // gRPC 端点走 HTTPS，<b>要求 Aspire 开发证书已被系统信任</b>。
            //
            // 未信任时（本项目踩过）：gRPC 握手在 TLS 阶段就失败，症状极具误导性——
            //   · exporter 不报任何错，BatchExportProcessor 静默丢弃失败批次
            //   · 面板「追踪」页签一片空白
            //   · 日志里只有 AppHost 启动时一句 warning：
            //     "No trusted Aspire development certificate was found."
            //
            // 新机器 / 重装系统 / 证书过期后链路会再次消失，修复只需一条命令：
            //     dotnet dev-certs https --trust
            // 执行后<b>必须重启 AppHost</b>——面板进程仍持有旧证书，只有重启才会加载新证书。
            // ================================================================
            builder.Services.AddOpenTelemetry()
                .UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);
        }
        else
        {
            // 没有 OTLP 端点时（脱离 AppHost 直接 dotnet run），数据发不出去，
            // 面板上永远空白。这里至少把 span 打到控制台，
            // 让「链路没数据」当场可见，而不是静悄悄地什么都不发生。
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddConsoleExporter())
                .WithMetrics(metrics => metrics.AddConsoleExporter());
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    /// <summary>
    /// 解析 OTLP 导出端点。
    ///
    /// 按优先级依次尝试以下变量名：
    /// <list type="number">
    ///   <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> —— OTel 标准变量。手工配置与生产环境用这个，
    ///         标准 SDK 直接认，放第一位</item>
    ///   <item><c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c> —— <b>Aspire 13.x 注入的 gRPC 端点</b>，
    ///         与本项目使用的 Grpc 协议配套，走 AppHost 启动时命中它</item>
    ///   <item><c>ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL</c> —— Aspire 为 HttpProtobuf 单独注入的变量，
    ///         仅在面板改用 HTTP 监听时才会是它</item>
    ///   <item><c>ASPIRE_DASHBOARD_OTLP_ENDPOINT</c> —— 部分版本的写法</item>
    /// </list>
    ///
    /// 全部读不到才判定为「没有后端」。
    /// </summary>
    private static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var candidates = new[]
        {
            configuration["OTEL_EXPORTER_OTLP_ENDPOINT"],
            configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"],
            configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"],
            configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT"],
        };

        foreach (var candidate in candidates)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }

}
