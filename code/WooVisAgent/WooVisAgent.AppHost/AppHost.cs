var builder = DistributedApplication.CreateBuilder(args);

var password = builder.AddParameter("pg-password", secret: true);
var postgresdb = builder.AddPostgres("postgres", password: password)
    .WithHostPort(5432)
    .WithDataVolume("woovisagent")
    // pgweb 是数据库管理界面，首次运行需要联网拉取 sosedoff/pgweb 镜像。
    // 当前网络访问 Docker Hub 及 daemon.json 里的镜像源都超时，该资源会一直 FailedToStart，
    // 所以先关掉；等网络恢复后取消下面这行注释即可。
    //.WithPgWeb(pgAdmin => pgAdmin.WithHostPort(5050))
    // 挂载初始化脚本：容器启动时会自动执行该目录下的 .sql 文件创建 erp_demo
    //.WithBindMount("./sql", "/docker-entrypoint-initdb.d")
    .AddDatabase("woovisagent");

// 注意：WithManagementPlugin(port) 里的 port 是「RabbitMQ 管理控制台(容器内 15672)映射到宿主机的端口」，
// 不是 AMQP 端口。之前传 5672 会和宿主机上已被其他 rabbitmq 容器占用的 5672 冲突，
// 导致 eventbus 容器 FailedToStart，进而 api / webui 因 WaitFor 一直卡在 Waiting。
// AMQP 端口通过配置项 Resources:eventbus:tcp:port 指定，见 appsettings.Development.json（默认 15500）。
var rabbitmq = builder.AddRabbitMQ("eventbus")
    .WithManagementPlugin(15673)
    .WithLifetime(ContainerLifetime.Persistent);

var httpapi = builder.AddProject<Projects.WooVisAgent_Api>("woovisagent-api")
    .WithUrl("swagger")
    .WaitFor(postgresdb)
    .WaitFor(rabbitmq)
    .WithReference(postgresdb)
    .WithReference(rabbitmq)
    // 把 OTLP 导出环境变量注入进去，这是 Aspire 面板「追踪」页签有数据的<b>前提</b>：
    // ServiceDefaults 里的 AddOpenTelemetryExporters 只在解析到端点时才挂 exporter，
    // 而不挂 WithOtlpExporter 就没有这些变量 —— 于是采集器正常、span 也在建，
    // 但没有出口，面板永远空白。漏掉这一行的症状就是「配了 OpenTelemetry 却看不到链路」。
    .WithOtlpExporter(OtlpProtocol.Grpc)
    // 把 SQL 语句写进 span。本地排查慢 SQL 时很有用，代价是 span 体积变大，
    // 因此默认关闭，需要时改成 "true" 或去掉这一行。
    .WithEnvironment("OTEL_RECORD_SQL", "true");

// 图片/视频生成工作服务：消费队列 WooVisAgent.GenerateWorker 中的任务并执行生成。
// 与 Api 共用同一套 PostgreSQL 与 RabbitMQ，产物落盘目录也一致（见 LocalStorageDefaults）。
// WaitFor(httpapi)：让 Api 先完成建表，避免两个进程同时首次建表造成竞争。
builder.AddProject<Projects.WooVisAgent_GenerateWorker>("woovisagent-generateworker")
    .WithReference(postgresdb)
    .WithReference(rabbitmq)
    .WaitFor(postgresdb)
    .WaitFor(rabbitmq)
    .WaitFor(httpapi)
    // 同样需要 OTLP 出口：生成任务的大模型调用全发生在这个进程里，
    // 不注入的话面板上只能看到 Api 侧的入站 span，看不到真正耗时的模型调用。
    .WithOtlpExporter(OtlpProtocol.Grpc)
    .WithEnvironment("OTEL_RECORD_SQL", "true");

// 前端目录 WooVisAgent.Web 目前还是空的（没有 package.json），
// 直接注册 Vite 会因为 `npm error enoent Could not read package.json` 启动失败，
// 所以这里做个判断：等前端工程建好（有 package.json）后会自动纳入编排，无需再改代码。
var webRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "WooVisAgent.Web"));
if (File.Exists(Path.Combine(webRoot, "package.json")))
{
    builder.AddViteApp("woovisagent-webui", "../WooVisAgent.Web")
        .WithEndpoint("http", endpointAnnotation =>
        {
            endpointAnnotation.Port = 5199;
        })
        .WaitFor(httpapi)
        .WithReference(httpapi);
}

builder.Build().Run();
