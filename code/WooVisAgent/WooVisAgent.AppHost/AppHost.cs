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
    .WithReference(rabbitmq);

// 图片/视频生成工作服务：消费队列 WooVisAgent.GenerateWorker 中的任务并执行生成。
// 与 Api 共用同一套 PostgreSQL 与 RabbitMQ，产物落盘目录也一致（见 LocalStorageDefaults）。
// WaitFor(httpapi)：让 Api 先完成建表，避免两个进程同时首次建表造成竞争。
builder.AddProject<Projects.WooVisAgent_GenerateWorker>("woovisagent-generateworker")
    .WithReference(postgresdb)
    .WithReference(rabbitmq)
    .WaitFor(postgresdb)
    .WaitFor(rabbitmq)
    .WaitFor(httpapi);

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
