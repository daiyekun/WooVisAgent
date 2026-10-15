var builder = DistributedApplication.CreateBuilder(args);

var password = builder.AddParameter("pg-password", secret: true);
var postgresdb = builder.AddPostgres("postgres", password: password)
    .WithHostPort(5432)
    .WithDataVolume("woovisagent")
    .WithPgWeb(pgAdmin => pgAdmin.WithHostPort(5050))
    // 挂载初始化脚本：容器启动时会自动执行该目录下的 .sql 文件创建 erp_demo
    //.WithBindMount("./sql", "/docker-entrypoint-initdb.d")
    .AddDatabase("woovisagent");

var rabbitmq = builder.AddRabbitMQ("eventbus")
    .WithManagementPlugin(5672)
    .WithLifetime(ContainerLifetime.Persistent);

var httpapi = builder.AddProject<Projects.WooVisAgent_Api>("woovisagent-api")
    .WithUrl("swagger")
    .WaitFor(postgresdb)
    .WaitFor(rabbitmq)
    .WithReference(postgresdb)
    .WithReference(rabbitmq);

builder.AddViteApp("woovisagent-webui", "../WooVisAgent.Web")
    .WithEndpoint("http", endpointAnnotation =>
    {
        endpointAnnotation.Port = 5199;
    })
    .WaitFor(httpapi)
    .WithReference(httpapi);

builder.Build().Run();
